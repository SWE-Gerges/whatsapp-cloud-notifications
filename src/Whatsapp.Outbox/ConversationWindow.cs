namespace Whatsapp.Outbox;

/// <summary>The last inbound customer message per tenant and recipient. It opens the 24 hour service window.</summary>
public sealed class ConversationWindow
{
    public string TenantId { get; set; } = string.Empty;

    public string Recipient { get; set; } = string.Empty;

    public DateTimeOffset LastInboundAt { get; set; }
}
