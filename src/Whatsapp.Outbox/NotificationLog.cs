using Whatsapp.Abstractions;

namespace Whatsapp.Outbox;

/// <summary>
/// One outbox row: a notification and everything known about its delivery. State changes after
/// creation happen through guarded SQL updates (see <see cref="OutboxProcessor"/>), not through tracked edits.
/// </summary>
public sealed class NotificationLog
{
    public const int MaxKeyLength = 100;
    public const int MaxErrorLength = 500;
    public const int MaxRecipientLength = 32;
    public const int MaxTemplateNameLength = 512;
    public const int MaxTemplateLanguageLength = 16;
    public const int MaxTextLength = 4096;

    public Guid Id { get; set; }

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Recipient in E.164 format.</summary>
    public string Recipient { get; set; } = string.Empty;

    public string EventKey { get; set; } = string.Empty;

    /// <summary>Caller supplied reference, shortened to <see cref="MaxKeyLength"/> characters.</summary>
    public string ReferenceKey { get; set; } = string.Empty;

    public NotificationKind Kind { get; set; }

    /// <summary>The template to send, or the fallback template of a free-text request.</summary>
    public string? TemplateName { get; set; }

    public string? TemplateLanguage { get; set; }

    public IReadOnlyList<string> TemplateParameters { get; set; } = [];

    public string? Text { get; set; }

    public NotificationStatus Status { get; set; }

    /// <summary>Number of send attempts started so far.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Identifies the worker that holds the lease; only that worker may complete the row.</summary>
    public Guid? LeaseToken { get; set; }

    public string? ProviderMessageId { get; set; }

    /// <summary>
    /// When the current delivery cycle began: set when a worker claims the row and when an administrator retries it.
    /// A receipt that is joined to the row only through the echoed notification id counts only if it was recorded
    /// at or after this instant, so the receipts of an earlier, failed cycle cannot decide the outcome of a retry.
    /// </summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>The template notification queued as the fallback when the provider rejected this free-text message for being outside the service window.</summary>
    public Guid? FallbackNotificationId { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
