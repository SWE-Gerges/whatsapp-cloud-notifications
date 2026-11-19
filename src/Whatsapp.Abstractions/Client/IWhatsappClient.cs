namespace Whatsapp.Abstractions;

/// <summary>
/// Sends messages to the Cloud API. All tenant specific settings travel with each call,
/// so an implementation holds no per-tenant mutable state.
/// </summary>
public interface IWhatsappClient
{
    /// <exception cref="WhatsappApiException">The provider rejected the message or could not be reached.</exception>
    Task<WhatsappSendResult> SendAsync(
        WhatsappMessage message,
        WhatsappTenantOptions options,
        CancellationToken cancellationToken);
}
