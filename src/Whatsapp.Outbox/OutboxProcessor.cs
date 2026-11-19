using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;
using Whatsapp.Templates;

namespace Whatsapp.Outbox;

/// <summary>
/// One dispatch cycle: recover rows whose lease expired, claim a batch of due rows, send them and record the
/// outcome on each row. Failures are written to the row and never thrown to the caller; only cancellation
/// (host shutdown) escapes, and the lease mechanism takes care of rows interrupted by it.
/// </summary>
public sealed class OutboxProcessor(
    IDbContextFactory<OutboxDbContext> dbFactory,
    IWhatsappClient client,
    ITenantOptionsResolver tenants,
    IConversationWindowPolicy windowPolicy,
    ITemplateRegistry templates,
    IReceiptEffects receiptEffects,
    IOutboxBackoff backoff,
    IOptions<OutboxOptions> options,
    IOptions<ReconciliationOptions> reconciliation,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger)
{
    /// <param name="NextAttemptDelay">
    /// When the row should be looked at again: the retry delay of a <c>Pending</c> row, or the grace period an
    /// <c>Unconfirmed</c> row waits for a receipt.
    /// </param>
    private readonly record struct Outcome(
        NotificationStatus Status,
        string? Error = null,
        string? ProviderMessageId = null,
        TimeSpan? NextAttemptDelay = null,
        Guid? FallbackNotificationId = null);

    /// <returns>The number of rows that were claimed and processed.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();

        await RecoverExpiredLeasesAsync(now, cancellationToken).ConfigureAwait(false);
        var claimed = await ClaimBatchAsync(now, settings, cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return 0;
        }

        await Parallel.ForEachAsync(
            claimed,
            new ParallelOptions { MaxDegreeOfParallelism = settings.Concurrency, CancellationToken = cancellationToken },
            async (row, token) => await ProcessRowAsync(row, settings, token).ConfigureAwait(false))
            .ConfigureAwait(false);

        return claimed.Count;
    }

    private async Task RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var reconcileAt = now + reconciliation.Value.GracePeriod;

        // A worker that died, or a shutdown in the middle of a send, may or may not have got the message out. Sending
        // again could deliver it twice, so the row is not requeued: it waits, as Unconfirmed, for a receipt that echoes
        // its id, and the reconciler decides when that wait is over.
        var unconfirmed = await db.NotificationLogs
            .Where(x => x.Status == NotificationStatus.Sending && x.LeaseExpiresAt <= now)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.Status, NotificationStatus.Unconfirmed)
                    .SetProperty(x => x.NextAttemptAt, reconcileAt)
                    .SetProperty(x => x.Error, "Unconfirmed: the lease expired before the outcome of the send was recorded, so the message may or may not have been sent.")
                    .SetProperty(x => x.LeaseToken, (Guid?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (unconfirmed > 0)
        {
            logger.LogWarning(
                "{Count} row(s) had an expired lease and are now Unconfirmed; they are not sent again until the reconciler decides",
                unconfirmed);
        }
    }

    private async Task<List<NotificationLog>> ClaimBatchAsync(DateTimeOffset now, OutboxOptions settings, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var candidateIds = await db.NotificationLogs
            .Where(x => x.Status == NotificationStatus.Pending && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .Select(x => x.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidateIds.Count == 0)
        {
            return [];
        }

        // The status predicate is repeated in the UPDATE: when two dispatchers pick the same candidates, the
        // database lets exactly one of them flip a row to Sending, and the other updates nothing.
        var token = Guid.NewGuid();
        var leaseExpiresAt = now + settings.LeaseDuration;
        await db.NotificationLogs
            .Where(x => candidateIds.Contains(x.Id) && x.Status == NotificationStatus.Pending && x.NextAttemptAt <= now)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.Status, NotificationStatus.Sending)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                    .SetProperty(x => x.LastAttemptAt, (DateTimeOffset?)now)
                    .SetProperty(x => x.LeaseToken, (Guid?)token)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)leaseExpiresAt)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return await db.NotificationLogs
            .AsNoTracking()
            .Where(x => x.LeaseToken == token)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ProcessRowAsync(NotificationLog row, OutboxOptions settings, CancellationToken cancellationToken)
    {
        Outcome outcome;
        try
        {
            outcome = await AttemptAsync(row, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Shutdown interrupted notification {NotificationId}; its lease will expire and the row will be recovered",
                row.Id);
            return;
        }
        catch (WhatsappApiException ex)
        {
            outcome = await FromApiExceptionAsync(row, ex, settings).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TenantNotConfiguredException or SecretNotFoundException
            or TemplateNotRegisteredException or TemplateValidationException or NotificationValidationException)
        {
            logger.LogWarning(
                "Notification {NotificationId} for tenant {TenantId} cannot be sent: {Reason}",
                row.Id,
                row.TenantId,
                ex.Message);
            outcome = new Outcome(NotificationStatus.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            // Anything unexpected before the request was sent (the client's own failures are handled in AttemptAsync)
            // is recorded on the row and retried within the normal attempt budget.
            logger.LogError(ex, "Unexpected error while preparing notification {NotificationId}", row.Id);
            outcome = RetryOrDeadLetter(row, $"Unexpected {ex.GetType().Name}: {ex.Message}", retryAfter: null, settings);
        }

        // The outcome is always written, even during shutdown: losing the record of a sent message would cause a resend.
        await CompleteAsync(row, outcome).ConfigureAwait(false);
    }

    private async Task<Outcome> AttemptAsync(NotificationLog row, CancellationToken cancellationToken)
    {
        var tenant = await tenants.ResolveAsync(row.TenantId, cancellationToken).ConfigureAwait(false);
        if (!tenant.Enabled)
        {
            return new Outcome(NotificationStatus.Skipped, "Tenant is disabled.");
        }

        var decision = await windowPolicy.DecideAsync(
            row.TenantId,
            row.Recipient,
            row.Kind,
            hasFallbackTemplate: !string.IsNullOrWhiteSpace(row.TemplateName),
            tenant.ServiceWindowEnabled,
            cancellationToken).ConfigureAwait(false);

        WhatsappMessage message;
        switch (decision.Kind)
        {
            case WindowDecisionKind.Skip:
                return new Outcome(NotificationStatus.Skipped, decision.Reason ?? "Skipped by the service window policy.");

            case WindowDecisionKind.SendFreeText:
                message = new WhatsappTextMessage(
                    row.Recipient,
                    row.Text ?? throw new NotificationValidationException("The free-text notification has no text."));
                break;

            default:
                message = BuildTemplateMessage(row, tenant);
                break;
        }

        // The provider echoes the notification id on every status webhook of this message.
        message = message with { CallbackData = row.Id.ToString("D") };

        try
        {
            var result = await client.SendAsync(message, tenant, cancellationToken).ConfigureAwait(false);
            return new Outcome(NotificationStatus.Sent, ProviderMessageId: result.MessageId);
        }
        catch (Exception ex) when (ex is not (WhatsappApiException or OperationCanceledException))
        {
            // The client reports every failure it understands as a WhatsappApiException. Anything else is a defect
            // inside it, and nothing proves the request did not leave, so the row is not sent again automatically.
            logger.LogError(ex, "The Cloud API client failed unexpectedly for notification {NotificationId}; the outcome of the send is unknown", row.Id);
            return Unconfirmed($"Unconfirmed: unexpected {ex.GetType().Name} while sending, so the message may or may not have been sent.");
        }
    }

    private Outcome Unconfirmed(string description) =>
        new(NotificationStatus.Unconfirmed, description, NextAttemptDelay: reconciliation.Value.GracePeriod);

    private WhatsappTemplateMessage BuildTemplateMessage(NotificationLog row, WhatsappTenantOptions tenant)
    {
        var name = row.TemplateName ?? throw new NotificationValidationException("The template notification has no template name.");
        var definition = templates.Get(name);

        var requestedLanguage = string.IsNullOrWhiteSpace(row.TemplateLanguage) ? tenant.DefaultTemplateLanguage : row.TemplateLanguage;
        TemplateValidator.EnsureValid(definition, requestedLanguage, row.TemplateParameters);

        // EnsureValid guarantees the language resolves to one of the template's own codes.
        return new WhatsappTemplateMessage(row.Recipient, name, definition.ResolveLanguage(requestedLanguage)!, row.TemplateParameters);
    }

    private async Task<Outcome> FromApiExceptionAsync(NotificationLog row, WhatsappApiException ex, OutboxOptions settings)
    {
        var description = $"{ex.Kind}: {ex.Message}";

        if (ex.OutcomeUnknown)
        {
            // The request may have been accepted. A blind retry could deliver the message twice, so the row waits for a
            // receipt instead; only a failure that provably never left, or an answer with an error body, is retried.
            logger.LogWarning(
                "The outcome of sending notification {NotificationId} is unknown ({Kind}); it is now Unconfirmed",
                row.Id,
                ex.Kind);
            return Unconfirmed($"Unconfirmed: {description}");
        }

        if (!ex.IsTransient)
        {
            logger.LogWarning(
                "Notification {NotificationId} was rejected permanently ({Kind}) for tenant {TenantId}",
                row.Id,
                ex.Kind,
                row.TenantId);

            if (ex.Kind == WhatsappErrorKind.OutsideServiceWindow
                && row.Kind == NotificationKind.FreeText
                && !string.IsNullOrWhiteSpace(row.TemplateName))
            {
                var fallbackId = await receiptEffects.QueueTemplateFallbackAsync(row, CancellationToken.None).ConfigureAwait(false);
                return new Outcome(
                    NotificationStatus.DeadLettered,
                    $"{description}. The template version was queued instead (see fallbackNotificationId).",
                    FallbackNotificationId: fallbackId);
            }

            return new Outcome(NotificationStatus.DeadLettered, description);
        }

        return RetryOrDeadLetter(row, description, ex.RetryAfter, settings);
    }

    private Outcome RetryOrDeadLetter(NotificationLog row, string description, TimeSpan? retryAfter, OutboxOptions settings)
    {
        if (row.Attempts >= settings.MaxAttempts)
        {
            logger.LogWarning(
                "Notification {NotificationId} is dead-lettered after {Attempts} attempts",
                row.Id,
                row.Attempts);
            return new Outcome(NotificationStatus.DeadLettered, $"Retries exhausted after {row.Attempts} attempts. {description}");
        }

        var delay = backoff.NextDelay(row.Attempts, retryAfter);
        logger.LogInformation(
            "Notification {NotificationId} failed attempt {Attempt}, retrying in {Delay}",
            row.Id,
            row.Attempts,
            delay);
        return new Outcome(NotificationStatus.Pending, description, NextAttemptDelay: delay);
    }

    private async Task CompleteAsync(NotificationLog row, Outcome outcome)
    {
        var now = timeProvider.GetUtcNow();
        var status = outcome.Status;
        var nextAttemptAt = outcome.NextAttemptDelay is { } delay ? now + delay : row.NextAttemptAt;
        var error = outcome.Error is null ? null : Truncate(SensitiveData.MaskPhonesInText(outcome.Error), NotificationLog.MaxErrorLength);
        var providerMessageId = outcome.ProviderMessageId;
        var fallbackId = outcome.FallbackNotificationId;
        var leaseToken = row.LeaseToken;
        var id = row.Id;

        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        var updated = await db.NotificationLogs
            .Where(x => x.Id == id && x.Status == NotificationStatus.Sending && x.LeaseToken == leaseToken)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.Error, error)
                    .SetProperty(x => x.ProviderMessageId, providerMessageId)
                    .SetProperty(x => x.NextAttemptAt, nextAttemptAt)
                    .SetProperty(x => x.FallbackNotificationId, x => fallbackId ?? x.FallbackNotificationId)
                    .SetProperty(x => x.LeaseToken, (Guid?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, now),
                CancellationToken.None)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            logger.LogWarning(
                "Notification {NotificationId} was no longer leased by this worker; the outcome {Status} was not recorded",
                id,
                status);
            return;
        }

        // Sent or Unconfirmed: in both a receipt may already be waiting (it beat this commit).
        if (status is NotificationStatus.Sent or NotificationStatus.Unconfirmed)
        {
            await ApplyReceiptsAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A receipt can arrive while the send is still in flight, and is then left alone because the row is leased.
    /// Applying the stored receipts right after the outcome is committed closes that gap from the other side.
    /// </summary>
    private async Task ApplyReceiptsAsync(Guid id)
    {
        try
        {
            await receiptEffects.ApplyAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or DbUpdateException or NotificationValidationException)
        {
            // The outcome itself is safely recorded. The receipts stay stored, and a redelivery or the next
            // reconciliation applies them.
            logger.LogWarning(ex, "Receipts for notification {NotificationId} could not be applied yet", id);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
