using Microsoft.EntityFrameworkCore;
using Whatsapp.Abstractions;

namespace Whatsapp.Outbox;

/// <param name="DeliveryStatus">When given, only notifications whose derived delivery status is this one.</param>
public sealed record NotificationQuery(
    NotificationStatus? Status = null,
    string? Recipient = null,
    string? TenantId = null,
    int Page = 1,
    int PageSize = 20,
    DeliveryStatus? DeliveryStatus = null);

/// <param name="DeliveryStatuses">The derived delivery status of every item that has one.</param>
public sealed record NotificationPage(
    IReadOnlyList<NotificationLog> Items,
    int Page,
    int PageSize,
    int Total,
    IReadOnlyDictionary<Guid, DeliveryStatus> DeliveryStatuses);

/// <summary>A notification together with the receipts that belong to it and the delivery status they add up to.</summary>
public sealed record NotificationDetails(
    NotificationLog Notification,
    IReadOnlyList<DeliveryReceipt> Receipts,
    DeliveryStatus? DeliveryStatus);

public enum RetryOutcome
{
    Requeued = 0,
    NotFound = 1,

    /// <summary>The row exists but is not in a state that allows a manual retry.</summary>
    NotRetryable = 2,
}

/// <summary>Read side of the outbox plus the manual retry operation, used by the HTTP API.</summary>
public interface INotificationStore
{
    /// <param name="tenantId">When given, only rows of that tenant are visible.</param>
    Task<NotificationLog?> GetAsync(Guid id, string? tenantId, CancellationToken cancellationToken);

    /// <summary>The notification with its receipt timeline and derived delivery status.</summary>
    /// <param name="tenantId">When given, only rows of that tenant are visible.</param>
    Task<NotificationDetails?> GetDetailsAsync(Guid id, string? tenantId, CancellationToken cancellationToken);

    Task<NotificationPage> ListAsync(NotificationQuery query, CancellationToken cancellationToken);

    /// <summary>Puts a Failed, DeadLettered or Unconfirmed row back in the queue with a fresh attempt budget.</summary>
    Task<RetryOutcome> RetryAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class NotificationStore(IDbContextFactory<OutboxDbContext> dbFactory, TimeProvider timeProvider) : INotificationStore
{
    public const int MaxPageSize = 100;

    public async Task<NotificationLog?> GetAsync(Guid id, string? tenantId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.NotificationLogs.AsNoTracking().Where(x => x.Id == id);
        if (tenantId is not null)
        {
            query = query.Where(x => x.TenantId == tenantId);
        }

        return await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationDetails?> GetDetailsAsync(Guid id, string? tenantId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.NotificationLogs.AsNoTracking().Where(x => x.Id == id);
        if (tenantId is not null)
        {
            query = query.Where(x => x.TenantId == tenantId);
        }

        var row = await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var receipts = await ReceiptJoin.LoadAsync(db, row, cancellationToken).ConfigureAwait(false);
        return new NotificationDetails(row, receipts, DeliveryStatusDerivation.Derive(receipts.Select(r => r.Status)));
    }

    public async Task<NotificationPage> ListAsync(NotificationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = db.NotificationLogs.AsNoTracking().AsQueryable();

        if (query.Status is { } status)
        {
            rows = rows.Where(x => x.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Recipient))
        {
            var recipient = query.Recipient.Trim();
            rows = rows.Where(x => x.Recipient == recipient);
        }

        if (!string.IsNullOrWhiteSpace(query.TenantId))
        {
            var tenantId = query.TenantId.Trim();
            rows = rows.Where(x => x.TenantId == tenantId);
        }

        if (query.DeliveryStatus is { } delivery)
        {
            rows = DeliveryFilter.Apply(rows, db.DeliveryReceipts, delivery);
        }

        var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await rows
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new NotificationPage(items, page, pageSize, total, await DeriveAsync(db, items, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>One query for the whole page: the receipts that can belong to any of its rows, matched in memory.</summary>
    private static async Task<IReadOnlyDictionary<Guid, DeliveryStatus>> DeriveAsync(
        OutboxDbContext db,
        List<NotificationLog> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new Dictionary<Guid, DeliveryStatus>();
        }

        var ids = items.Select(x => (Guid?)x.Id).ToList();
        var providerIds = items.Where(x => x.ProviderMessageId is not null).Select(x => x.ProviderMessageId!).ToList();
        var candidates = await db.DeliveryReceipts
            .AsNoTracking()
            .Where(r => ids.Contains(r.NotificationId) || providerIds.Contains(r.ProviderMessageId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<Guid, DeliveryStatus>();
        foreach (var row in items)
        {
            var derived = DeliveryStatusDerivation.Derive(candidates.Where(r => ReceiptJoin.Matches(row, r)).Select(r => r.Status));
            if (derived is { } status)
            {
                result[row.Id] = status;
            }
        }

        return result;
    }

    public async Task<RetryOutcome> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // A retry starts a new delivery cycle: the provider id of the earlier message is forgotten and LastAttemptAt moves
        // to now, so the receipts of the earlier message can no longer be mistaken for the outcome of the new one.
        var updated = await db.NotificationLogs
            .Where(x => x.Id == id && RetryableStatuses.Contains(x.Status))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(x => x.Status, NotificationStatus.Pending)
                    .SetProperty(x => x.Attempts, 0)
                    .SetProperty(x => x.NextAttemptAt, now)
                    .SetProperty(x => x.LastAttemptAt, (DateTimeOffset?)now)
                    .SetProperty(x => x.ProviderMessageId, (string?)null)
                    .SetProperty(x => x.LeaseToken, (Guid?)null)
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        if (updated > 0)
        {
            return RetryOutcome.Requeued;
        }

        var exists = await db.NotificationLogs.AnyAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        return exists ? RetryOutcome.NotRetryable : RetryOutcome.NotFound;
    }

    private static readonly NotificationStatus[] RetryableStatuses =
    [
        NotificationStatus.Failed,
        NotificationStatus.DeadLettered,
        NotificationStatus.Unconfirmed,
    ];
}

/// <summary>
/// The SQL form of "derived delivery status equals X". It follows the same precedence as
/// <see cref="DeliveryStatusDerivation"/> (failed, then read, delivered, sent) and the same join as
/// <see cref="ReceiptJoin"/>; a test keeps the three in agreement.
/// </summary>
internal static class DeliveryFilter
{
    public static IQueryable<NotificationLog> Apply(IQueryable<NotificationLog> rows, IQueryable<DeliveryReceipt> receipts, DeliveryStatus wanted)
    {
        return wanted switch
        {
            DeliveryStatus.Failed => rows.Where(Has(receipts, ReceiptStatuses.Failed)),
            DeliveryStatus.Read => rows
                .Where(Has(receipts, ReceiptStatuses.Read))
                .Where(Lacks(receipts, ReceiptStatuses.Failed)),
            DeliveryStatus.Delivered => rows
                .Where(Has(receipts, ReceiptStatuses.Delivered))
                .Where(Lacks(receipts, ReceiptStatuses.Read))
                .Where(Lacks(receipts, ReceiptStatuses.Failed)),
            DeliveryStatus.Sent => rows
                .Where(Has(receipts, ReceiptStatuses.Sent))
                .Where(Lacks(receipts, ReceiptStatuses.Delivered))
                .Where(Lacks(receipts, ReceiptStatuses.Read))
                .Where(Lacks(receipts, ReceiptStatuses.Failed)),
            _ => throw new ArgumentOutOfRangeException(nameof(wanted), wanted, "Unknown delivery status."),
        };
    }

    private static System.Linq.Expressions.Expression<Func<NotificationLog, bool>> Has(IQueryable<DeliveryReceipt> receipts, string status) =>
        x => receipts.Any(r => r.Status == status
            && r.TenantId == x.TenantId
            && ((r.NotificationId == x.Id && (x.LastAttemptAt == null || r.RecordedAt >= x.LastAttemptAt))
                || (x.ProviderMessageId != null && r.ProviderMessageId == x.ProviderMessageId)));

    private static System.Linq.Expressions.Expression<Func<NotificationLog, bool>> Lacks(IQueryable<DeliveryReceipt> receipts, string status) =>
        x => !receipts.Any(r => r.Status == status
            && r.TenantId == x.TenantId
            && ((r.NotificationId == x.Id && (x.LastAttemptAt == null || r.RecordedAt >= x.LastAttemptAt))
                || (x.ProviderMessageId != null && r.ProviderMessageId == x.ProviderMessageId)));
}
