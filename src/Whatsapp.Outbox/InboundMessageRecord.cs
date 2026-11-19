namespace Whatsapp.Outbox;

/// <summary>
/// An inbound customer message, reduced to the minimum: who, when and what kind. The text, media and any other
/// content of the message are discarded and never reach the database.
/// </summary>
public sealed class InboundMessageRecord
{
    public const int MaxMessageIdLength = 200;
    public const int MaxTypeLength = 32;

    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    /// <summary>The message id given by the provider. Together with the tenant it identifies the message, so a redelivery is recognised.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>The sender in E.164 format.</summary>
    public string Sender { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    /// <summary>When the customer sent the message, as stated by the provider.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
