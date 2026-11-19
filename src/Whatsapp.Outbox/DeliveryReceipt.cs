namespace Whatsapp.Outbox;

/// <summary>
/// One status event for a provider message. Receipts are only ever appended: they are not tied to a notification
/// row when they are written, so they can arrive before the row is marked <c>Sent</c>, or for a message this service
/// never sent, and are joined to a notification when it is read.
/// </summary>
public sealed class DeliveryReceipt
{
    public const int MaxProviderMessageIdLength = 200;
    public const int MaxStatusLength = 32;
    public const int MaxErrorTitleLength = 200;

    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string ProviderMessageId { get; set; } = string.Empty;

    /// <summary>The provider status in lower case (<c>sent</c>, <c>delivered</c>, <c>read</c>, <c>failed</c>, or one not known here).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The timestamp of the event, as stated by the provider.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The notification id echoed back through <c>biz_opaque_callback_data</c>, when there was one.</summary>
    public Guid? NotificationId { get; set; }

    public int? ErrorCode { get; set; }

    public string? ErrorTitle { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
