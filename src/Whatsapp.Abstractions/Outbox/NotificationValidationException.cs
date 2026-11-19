namespace Whatsapp.Abstractions;

/// <summary>A notification request is malformed and cannot be enqueued.</summary>
public sealed class NotificationValidationException(string message) : InvalidOperationException(message);
