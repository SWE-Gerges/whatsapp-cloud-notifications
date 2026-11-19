using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whatsapp.Abstractions;

namespace Whatsapp.Outbox;

public sealed class ReconciliationOptions
{
    public const string SectionName = "Whatsapp:Reconciliation";

    /// <summary>Turn the background reconciler off, for example in tests that drive it directly.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long an <see cref="NotificationStatus.Unconfirmed"/> notification waits for a receipt that echoes its id before
    /// <see cref="OnGraceExpired"/> applies.
    /// </summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Pause between reconciliation passes.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>What happens to a notification that is still unconfirmed when the grace period is over.</summary>
    public UnconfirmedPolicy OnGraceExpired { get; set; } = UnconfirmedPolicy.DeadLetter;

    /// <summary>The most unconfirmed notifications examined in one pass.</summary>
    public int BatchSize { get; set; } = 200;
}

/// <summary>The trade-off for a send whose outcome is unknown: possibly never delivered, or possibly delivered twice.</summary>
public enum UnconfirmedPolicy
{
    /// <summary>
    /// At most once. The notification is dead-lettered with the reason <c>unconfirmed</c> and is never sent again
    /// automatically; an administrator can retry it after checking.
    /// </summary>
    DeadLetter = 0,

    /// <summary>
    /// At least once. The notification goes back to <see cref="NotificationStatus.Pending"/> and is sent again, which
    /// delivers the message twice when the first request had in fact been accepted. Bounded by the attempt budget.
    /// </summary>
    Resend = 1,
}

internal sealed class ReconciliationOptionsValidator : IValidateOptions<ReconciliationOptions>
{
    public ValidateOptionsResult Validate(string? name, ReconciliationOptions options)
    {
        var failures = new List<string>();
        if (options.GracePeriod < TimeSpan.Zero)
        {
            failures.Add("Whatsapp:Reconciliation:GracePeriod must not be negative.");
        }

        if (options.PollInterval <= TimeSpan.Zero)
        {
            failures.Add("Whatsapp:Reconciliation:PollInterval must be positive.");
        }

        if (options.BatchSize is < 1 or > 10_000)
        {
            failures.Add("Whatsapp:Reconciliation:BatchSize must be between 1 and 10000.");
        }

        if (!Enum.IsDefined(options.OnGraceExpired))
        {
            failures.Add("Whatsapp:Reconciliation:OnGraceExpired must be DeadLetter or Resend.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public sealed record ReconciliationResult(int Examined, int DeadLettered, int Requeued);

/// <summary>
/// One reconciliation pass over the notifications whose send was never confirmed. Stored receipts are applied first, so
/// a message that was in fact delivered is never dead-lettered or sent again because the receipt happened to arrive
/// early. Only then are the notifications whose grace period is over resolved by the configured policy. Every change is
/// a guarded update on the status, so a pass that races with a receipt, another reconciler or the dispatcher has
/// exactly one winner.
/// </summary>
public sealed class ReconciliationProcessor(
    IDbContextFactory<OutboxDbContext> dbFactory,
    IReceiptEffects receiptEffects,
    IOptions<ReconciliationOptions> reconciliation,
    IOptions<OutboxOptions> outbox,
    TimeProvider timeProvider,
    ILogger<ReconciliationProcessor> logger)
{
    /// <summary>Prefix of the error stored on a notification that was dead-lettered by the reconciler.</summary>
    public const string UnconfirmedReason = "unconfirmed";

    public async Task<ReconciliationResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = reconciliation.Value;
        var now = timeProvider.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var unconfirmed = await db.NotificationLogs
            .AsNoTracking()
            .Where(x => x.Status == NotificationStatus.Unconfirmed)
            .OrderBy(x => x.NextAttemptAt)
            .Select(x => x.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var id in unconfirmed)
        {
            await receiptEffects.ApplyAsync(id, cancellationToken).ConfigureAwait(false);
        }

        if (unconfirmed.Count == 0)
        {
            return new ReconciliationResult(0, 0, 0);
        }

        var (deadLettered, requeued) = settings.OnGraceExpired == UnconfirmedPolicy.Resend
            ? await ResendAsync(db, now, cancellationToken).ConfigureAwait(false)
            : (await DeadLetterAsync(db, now, cancellationToken).ConfigureAwait(false), 0);

        if (deadLettered + requeued > 0)
        {
            logger.LogWarning(
                "{DeadLettered} unconfirmed notification(s) were dead-lettered and {Requeued} were queued to be sent again after the grace period of {Grace}",
                deadLettered,
                requeued,
                settings.GracePeriod);
        }

        return new ReconciliationResult(unconfirmed.Count, deadLettered, requeued);
    }

    private static Task<int> DeadLetterAsync(OutboxDbContext db, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.NotificationLogs
            .Where(x => x.Status == NotificationStatus.Unconfirmed && x.NextAttemptAt <= now)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.Status, NotificationStatus.DeadLettered)
                    .SetProperty(x => x.Error, UnconfirmedReason + ": no receipt arrived within the grace period, so it is not known whether the message was delivered. Check before retrying.")
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

    private async Task<(int DeadLettered, int Requeued)> ResendAsync(OutboxDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var maxAttempts = outbox.Value.MaxAttempts;

        var requeued = await db.NotificationLogs
            .Where(x => x.Status == NotificationStatus.Unconfirmed && x.NextAttemptAt <= now && x.Attempts < maxAttempts)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.Status, NotificationStatus.Pending)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.Error, UnconfirmedReason + ": no receipt arrived within the grace period; the message is sent again as configured.")
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        var deadLettered = await db.NotificationLogs
            .Where(x => x.Status == NotificationStatus.Unconfirmed && x.NextAttemptAt <= now && x.Attempts >= maxAttempts)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.Status, NotificationStatus.DeadLettered)
                    .SetProperty(x => x.Error, UnconfirmedReason + ": no receipt arrived within the grace period and the attempts are used up, so it is not sent again.")
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        return (deadLettered, requeued);
    }
}

/// <summary>Background loop that repeatedly runs <see cref="ReconciliationProcessor"/>. It never lets an error stop the host.</summary>
public sealed class ReconciliationService(
    ReconciliationProcessor processor,
    IOptions<ReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<ReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Reconciliation is disabled");
            return;
        }

        logger.LogInformation(
            "Reconciliation started (grace period {Grace}, after the grace period: {Policy})",
            settings.GracePeriod,
            settings.OnGraceExpired);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(settings.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A reconciliation pass failed; it will be repeated");
                try
                {
                    await Task.Delay(settings.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Reconciliation stopped");
    }
}
