using Microsoft.EntityFrameworkCore;
using Whatsapp.Abstractions;

namespace Whatsapp.Outbox;

/// <summary>
/// Stores an inbound message once and opens the sender's service window from the provider's timestamp. The demo
/// endpoint and the webhook both end up here, so they cannot disagree about what a message does to a window.
/// </summary>
internal sealed class InboundMessageRecorder(
    IDbContextFactory<OutboxDbContext> dbFactory,
    IConversationWindowStore windows,
    TimeProvider timeProvider) : IInboundMessageRecorder
{
    private static readonly string[] Columns = ["Id", "TenantId", "MessageId", "Sender", "Type", "ReceivedAt", "RecordedAt"];

    private static readonly string[] KeyColumns = ["TenantId", "MessageId"];

    public async Task<RecordResult> RecordAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        var inserted = await ConflictFreeInsert.ExecuteAsync(
            db.Database,
            "InboundMessages",
            Columns,
            [
                Guid.CreateVersion7(now),
                message.TenantId,
                Truncate(message.MessageId, InboundMessageRecord.MaxMessageIdLength),
                message.Sender,
                Truncate(message.Type, InboundMessageRecord.MaxTypeLength),
                message.ReceivedAt.UtcDateTime,
                now.UtcDateTime,
            ],
            KeyColumns,
            cancellationToken).ConfigureAwait(false);

        // Applied for a duplicate as well: the step is idempotent, and skipping it would leave the window closed if the
        // process died between the two writes of the first delivery.
        await windows.RecordInboundAsync(message.TenantId, message.Sender, message.ReceivedAt, cancellationToken).ConfigureAwait(false);

        return inserted == 1 ? RecordResult.Recorded : RecordResult.Duplicate;
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// Appends delivery receipts without ever rejecting one, then applies what they say to the notifications they belong
/// to. A receipt for a message this service did not send, or one that arrives before the dispatcher has committed
/// <c>Sent</c>, is stored all the same and joined when the notification is read.
/// </summary>
internal sealed class DeliveryReceiptRecorder(
    IDbContextFactory<OutboxDbContext> dbFactory,
    IReceiptEffects effects,
    TimeProvider timeProvider) : IDeliveryReceiptRecorder
{
    private static readonly string[] Columns =
    [
        "Id", "TenantId", "ProviderMessageId", "Status", "OccurredAt", "NotificationId", "ErrorCode", "ErrorTitle", "RecordedAt",
    ];

    private static readonly string[] KeyColumns = ["ProviderMessageId", "Status", "OccurredAt"];

    public async Task<RecordResult> RecordAsync(DeliveryReceiptInput receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        var providerMessageId = Truncate(receipt.ProviderMessageId, DeliveryReceipt.MaxProviderMessageIdLength);
        var inserted = await ConflictFreeInsert.ExecuteAsync(
            db.Database,
            "DeliveryReceipts",
            Columns,
            [
                Guid.CreateVersion7(now),
                receipt.TenantId,
                providerMessageId,
                Truncate(receipt.Status, DeliveryReceipt.MaxStatusLength),
                receipt.OccurredAt.UtcDateTime,
                receipt.NotificationId,
                receipt.ErrorCode,
                receipt.ErrorTitle is null ? null : Truncate(receipt.ErrorTitle, DeliveryReceipt.MaxErrorTitleLength),
                now.UtcDateTime,
            ],
            KeyColumns,
            cancellationToken).ConfigureAwait(false);

        // Also for a duplicate: the effects are idempotent, and a delivery that died between the two steps is repaired
        // by the provider's redelivery.
        await effects.ApplyForReceiptAsync(receipt.TenantId, providerMessageId, receipt.NotificationId, cancellationToken).ConfigureAwait(false);

        return inserted == 1 ? RecordResult.Recorded : RecordResult.Duplicate;
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
