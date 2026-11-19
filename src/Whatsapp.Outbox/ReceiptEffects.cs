using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whatsapp.Abstractions;

namespace Whatsapp.Outbox;

/// <summary>
/// Applies what the stored receipts say about a notification. The effects are one idempotent function of the row and
/// its receipts, so they can be (and are) applied from several places: when a receipt arrives, after the dispatcher has
/// committed an outcome (a receipt may have beaten it), and by the reconciler. Whichever happens last is the one that
/// sees both halves.
/// </summary>
public interface IReceiptEffects
{
    /// <summary>Applies the effects on every notification the receipt belongs to.</summary>
    Task ApplyForReceiptAsync(string tenantId, string providerMessageId, Guid? notificationId, CancellationToken cancellationToken);

    /// <summary>Re-evaluates one notification against the receipts stored for it.</summary>
    Task ApplyAsync(Guid notificationId, CancellationToken cancellationToken);

    /// <summary>
    /// Queues the template version of a free-text notification that the provider refused for being outside the service
    /// window. The key is derived from the original's id, so asking again returns the notification queued the first time.
    /// </summary>
    /// <returns>The id of the template notification.</returns>
    Task<Guid> QueueTemplateFallbackAsync(NotificationLog original, CancellationToken cancellationToken);
}

/// <summary>The rule that ties receipts to a notification.</summary>
internal static class ReceiptJoin
{
    /// <summary>
    /// A receipt belongs to a notification of the same tenant when it names the notification's provider message id, or
    /// when it carries the notification's id through <c>biz_opaque_callback_data</c> and was recorded during the current
    /// delivery cycle. Receipts are stored independently of notifications, so this rule is applied when they are read.
    /// </summary>
    public static bool Matches(NotificationLog row, DeliveryReceipt receipt) =>
        receipt.TenantId == row.TenantId
        && ((receipt.NotificationId == row.Id && (row.LastAttemptAt is not { } since || receipt.RecordedAt >= since))
            || (row.ProviderMessageId is not null && receipt.ProviderMessageId == row.ProviderMessageId));

    /// <summary>Loads the receipts that can belong to the row, in the order they happened.</summary>
    public static async Task<List<DeliveryReceipt>> LoadAsync(OutboxDbContext db, NotificationLog row, CancellationToken cancellationToken)
    {
        var tenantId = row.TenantId;
        var id = row.Id;
        var providerMessageId = row.ProviderMessageId;

        var query = db.DeliveryReceipts.AsNoTracking().Where(r => r.TenantId == tenantId);
        query = providerMessageId is null
            ? query.Where(r => r.NotificationId == id)
            : query.Where(r => r.NotificationId == id || r.ProviderMessageId == providerMessageId);

        var candidates = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. candidates.Where(r => Matches(row, r)).OrderBy(r => r.OccurredAt).ThenBy(r => r.RecordedAt).ThenBy(r => r.Id)];
    }
}

internal sealed class ReceiptEffects(
    IDbContextFactory<OutboxDbContext> dbFactory,
    INotificationEnqueuer enqueuer,
    TimeProvider timeProvider,
    ILogger<ReceiptEffects> logger) : IReceiptEffects
{
    /// <summary>Event key of the template notification queued in place of a free-text one the provider refused.</summary>
    public const string FallbackEventKey = "template-fallback";

    public async Task ApplyForReceiptAsync(string tenantId, string providerMessageId, Guid? notificationId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = db.NotificationLogs.AsNoTracking().Where(x => x.TenantId == tenantId);
        query = notificationId is { } id
            ? query.Where(x => x.Id == id || x.ProviderMessageId == providerMessageId)
            : query.Where(x => x.ProviderMessageId == providerMessageId);

        var ids = await query.Select(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var target in ids)
        {
            await ApplyAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ApplyAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var row = await db.NotificationLogs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == notificationId, cancellationToken).ConfigureAwait(false);
        if (row is null || !CanBeResolvedByReceipts(row.Status))
        {
            return;
        }

        var receipts = await ReceiptJoin.LoadAsync(db, row, cancellationToken).ConfigureAwait(false);

        // Once the row knows its provider message id, only that message's receipts speak for it. Before that (the
        // outcome of the send is unknown) the echoed notification id is all there is to go by.
        var evidence = row.ProviderMessageId is null
            ? receipts
            : [.. receipts.Where(r => r.ProviderMessageId == row.ProviderMessageId)];

        if (evidence.FirstOrDefault(r => r.Status == ReceiptStatuses.Failed) is { } failure)
        {
            await FailAsync(db, row, failure, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (row.ProviderMessageId is null
            && evidence.FirstOrDefault(r => DeliveryStatusDerivation.Parse(r.Status) is not null) is { } accepted)
        {
            await MarkSentAsync(db, row, accepted, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A row in flight is left to the worker that holds it; it applies the receipts itself once it has committed. Rows
    /// that never reached the provider (<c>Skipped</c>, a local <c>Failed</c>) have no receipts to apply.
    /// </summary>
    private static bool CanBeResolvedByReceipts(NotificationStatus status) =>
        status is NotificationStatus.Sent
            or NotificationStatus.Unconfirmed
            or NotificationStatus.Pending
            or NotificationStatus.DeadLettered;

    private async Task MarkSentAsync(OutboxDbContext db, NotificationLog row, DeliveryReceipt evidence, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var observed = row.Status;
        var providerMessageId = evidence.ProviderMessageId;

        // The guard on the status and on the empty provider id makes a lost race harmless: the other writer won.
        var updated = await db.NotificationLogs
            .Where(x => x.Id == row.Id && x.Status == observed && x.ProviderMessageId == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.Status, NotificationStatus.Sent)
                    .SetProperty(x => x.ProviderMessageId, providerMessageId)
                    .SetProperty(x => x.Error, (string?)null)
                    .SetProperty(x => x.LeaseToken, (Guid?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (updated > 0)
        {
            logger.LogInformation(
                "A {Receipt} receipt confirmed notification {NotificationId}, which was {Observed}; it is now Sent as {ProviderMessageId}",
                evidence.Status,
                row.Id,
                observed,
                providerMessageId);
        }
    }

    private async Task FailAsync(OutboxDbContext db, NotificationLog row, DeliveryReceipt failure, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var observed = row.Status;
        var providerMessageId = failure.ProviderMessageId;

        Guid? fallbackId = null;
        if (failure.ErrorCode == GraphErrorCodes.OutsideServiceWindow
            && row.Kind == NotificationKind.FreeText
            && !string.IsNullOrWhiteSpace(row.TemplateName))
        {
            fallbackId = await QueueTemplateFallbackAsync(row, cancellationToken).ConfigureAwait(false);
        }

        var error = Describe(failure, fallbackId);
        var updated = await db.NotificationLogs
            .Where(x => x.Id == row.Id && x.Status == observed)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.Status, NotificationStatus.Failed)
                    .SetProperty(x => x.Error, error)
                    .SetProperty(x => x.ProviderMessageId, x => x.ProviderMessageId ?? providerMessageId)
                    .SetProperty(x => x.FallbackNotificationId, fallbackId)
                    .SetProperty(x => x.LeaseToken, (Guid?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (updated > 0)
        {
            logger.LogWarning(
                "Notification {NotificationId} was reported undeliverable (code {Code}); it is now Failed{Fallback}",
                row.Id,
                failure.ErrorCode,
                fallbackId is null ? string.Empty : $" and its template fallback {fallbackId} was queued");
        }
    }

    public async Task<Guid> QueueTemplateFallbackAsync(NotificationLog original, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);

        // One fallback per original notification: its id is the reference key, so a replayed receipt or a repeated
        // attempt finds the notification queued the first time instead of queueing another.
        var queued = await enqueuer.EnqueueAsync(
            new EnqueueNotification(
                original.TenantId,
                original.Recipient,
                FallbackEventKey,
                original.Id.ToString("D"),
                NotificationKind.Template,
                original.TemplateName,
                original.TemplateLanguage,
                original.TemplateParameters),
            cancellationToken).ConfigureAwait(false);
        return queued.Id;
    }

    private static string Describe(DeliveryReceipt failure, Guid? fallbackId)
    {
        var reason = $"Delivery failed: {failure.ErrorCode?.ToString() ?? "no code"} {failure.ErrorTitle}".TrimEnd();
        if (fallbackId is not null)
        {
            // The id itself is in FallbackNotificationId; a GUID in free text can look like a phone number to the masking.
            reason += ". The template version was queued instead (see fallbackNotificationId).";
        }

        var masked = SensitiveData.MaskPhonesInText(reason);
        return masked.Length <= NotificationLog.MaxErrorLength ? masked : masked[..NotificationLog.MaxErrorLength];
    }
}
