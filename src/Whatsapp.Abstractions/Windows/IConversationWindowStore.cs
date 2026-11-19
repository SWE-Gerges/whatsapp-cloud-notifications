namespace Whatsapp.Abstractions;

/// <summary>Tracks the last inbound message per recipient to decide whether the 24 hour window is open.</summary>
public interface IConversationWindowStore
{
    Task<bool> IsOpenAsync(string tenantId, string recipient, CancellationToken cancellationToken);

    /// <summary>Records an inbound customer message and (re)opens the window.</summary>
    Task RecordInboundAsync(string tenantId, string recipient, DateTimeOffset receivedAt, CancellationToken cancellationToken);
}
