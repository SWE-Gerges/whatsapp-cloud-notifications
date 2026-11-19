namespace Whatsapp.Abstractions;

/// <summary>An outbound message addressed to a single recipient in E.164 format.</summary>
public abstract record WhatsappMessage(string To)
{
    /// <summary>
    /// Sent as <c>biz_opaque_callback_data</c>. The provider returns it on every status webhook for the message, which is
    /// how a receipt finds the notification it belongs to even when the send itself was never confirmed.
    /// </summary>
    public string? CallbackData { get; init; }
}

/// <summary>A free-form text message (only valid inside the service window).</summary>
public sealed record WhatsappTextMessage(string To, string Body) : WhatsappMessage(To);

/// <summary>An approved template message with ordered body parameters.</summary>
public sealed record WhatsappTemplateMessage(
    string To,
    string TemplateName,
    string LanguageCode,
    IReadOnlyList<string> Parameters) : WhatsappMessage(To);

/// <summary>The provider acknowledgement of an accepted message.</summary>
public sealed record WhatsappSendResult(string MessageId, string? ContactWaId);
