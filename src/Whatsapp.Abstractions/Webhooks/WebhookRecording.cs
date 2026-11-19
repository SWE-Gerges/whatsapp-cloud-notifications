namespace Whatsapp.Abstractions;

/// <summary>An inbound customer message, reduced to what the service keeps. The text of the message is never stored.</summary>
/// <param name="Sender">The sender in E.164 format.</param>
/// <param name="Type">The provider's message type (<c>text</c>, <c>image</c>, ...), or <c>simulated</c> for the demo endpoint.</param>
/// <param name="ReceivedAt">When the message was sent, as stated by the provider.</param>
public sealed record InboundMessage(string TenantId, string MessageId, string Sender, string Type, DateTimeOffset ReceivedAt);

public enum RecordResult
{
    /// <summary>The event had not been seen before.</summary>
    Recorded = 0,

    /// <summary>The event was already recorded; a redelivery changes nothing.</summary>
    Duplicate = 1,
}

/// <summary>Records inbound messages and opens the 24 hour service window for their senders.</summary>
public interface IInboundMessageRecorder
{
    /// <summary>
    /// Stores the message once (a redelivery is a duplicate) and moves the sender's window forward. The window is
    /// advanced even for a duplicate, because that step is idempotent and a crash between the two writes must
    /// not leave the window closed forever.
    /// </summary>
    Task<RecordResult> RecordAsync(InboundMessage message, CancellationToken cancellationToken);
}

/// <summary>One status event for a message, as delivered by a status webhook.</summary>
/// <param name="Status">The provider status in lower case: <c>sent</c>, <c>delivered</c>, <c>read</c>, <c>failed</c>, or one this service does not know.</param>
/// <param name="OccurredAt">The provider's timestamp for the event.</param>
/// <param name="NotificationId">The id this service put into <c>biz_opaque_callback_data</c>, when it came back.</param>
public sealed record DeliveryReceiptInput(
    string TenantId,
    string ProviderMessageId,
    string Status,
    DateTimeOffset OccurredAt,
    Guid? NotificationId,
    int? ErrorCode,
    string? ErrorTitle);

/// <summary>Appends delivery receipts. A receipt is never rejected, whatever it refers to.</summary>
public interface IDeliveryReceiptRecorder
{
    /// <summary>
    /// Appends the receipt (a redelivery is a duplicate) and applies its effects on the notification it belongs to.
    /// The effects run even for a duplicate because they are idempotent and a crash between the two steps must be repairable by redelivery.
    /// </summary>
    Task<RecordResult> RecordAsync(DeliveryReceiptInput receipt, CancellationToken cancellationToken);
}
