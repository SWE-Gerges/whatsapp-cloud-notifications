namespace Whatsapp.Abstractions;

/// <summary>Durably records a notification so the dispatcher can deliver it exactly once.</summary>
public interface INotificationEnqueuer
{
    /// <summary>
    /// Claim-first enqueue: the row is inserted before anything is sent. A second request with the same
    /// (tenant, recipient, event key, reference key) returns the existing row with <c>Duplicate = true</c>.
    /// </summary>
    Task<EnqueueResult> EnqueueAsync(EnqueueNotification request, CancellationToken cancellationToken);
}

/// <summary>A notification to deliver. Template requests carry the template, free-text requests the text.</summary>
/// <remarks>A free-text request may also carry a template, used as the fallback outside the service window.</remarks>
public sealed record EnqueueNotification(
    string TenantId,
    string Recipient,
    string EventKey,
    string ReferenceKey,
    NotificationKind Kind,
    string? TemplateName = null,
    string? TemplateLanguage = null,
    IReadOnlyList<string>? TemplateParameters = null,
    string? Text = null);

public sealed record EnqueueResult(Guid Id, NotificationStatus Status, bool Duplicate);
