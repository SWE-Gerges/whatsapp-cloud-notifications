using Whatsapp.Abstractions;
using Whatsapp.Abstractions.Bookings;

namespace Whatsapp.Api.Contracts;

/// <summary>Request to queue a custom notification. The tenant is taken from the API key.</summary>
public sealed record EnqueueNotificationRequest
{
    /// <summary>Recipient phone number: E.164 (<c>+12025550100</c>) or national format together with <see cref="DefaultRegion"/>.</summary>
    public required string Recipient { get; init; }

    /// <summary>ISO 3166-1 alpha-2 region used to interpret a recipient given in national format.</summary>
    public string? DefaultRegion { get; init; }

    public required NotificationKind Kind { get; init; }

    /// <summary>Identifies the kind of event, for example <c>order-shipped</c>. Part of the idempotency key.</summary>
    public required string EventKey { get; init; }

    /// <summary>Identifies the subject of the event, for example an order id. Part of the idempotency key.</summary>
    public required string ReferenceKey { get; init; }

    /// <summary>The template to send. For free-text requests it is the fallback used when the service window is closed.</summary>
    public TemplateRequest? Template { get; init; }

    /// <summary>Free-text body. Use either <see cref="Text"/> or <see cref="TextVariants"/>.</summary>
    public string? Text { get; init; }

    /// <summary>Free-text bodies by language code (<c>en</c>, <c>ar</c>); the recipient's preferred language picks one.</summary>
    public Dictionary<string, string>? TextVariants { get; init; }

    /// <summary>Values for <c>{placeholder}</c> tokens in the text.</summary>
    public Dictionary<string, string>? Placeholders { get; init; }

    public string? PreferredLanguage { get; init; }
}

public sealed record TemplateRequest
{
    public required string Name { get; init; }

    /// <summary>Template language code. When omitted the tenant default language is used.</summary>
    public string? LanguageCode { get; init; }

    public List<string> Parameters { get; init; } = [];
}

public sealed record EnqueueNotificationResponse(Guid Id, NotificationStatus Status, bool Duplicate);

/// <param name="NextAttemptAt">
/// When the service next acts on the notification. For an <c>Unconfirmed</c> notification it is the moment the reconciler
/// decides what happens next (the end of the grace period), not a scheduled send.
/// </param>
/// <param name="DeliveryStatus">What the provider's status webhooks add up to; absent until a receipt has arrived.</param>
/// <param name="FallbackNotificationId">The template notification queued in place of this free-text one after a window error.</param>
/// <param name="Receipts">The receipt timeline. Present on a single notification, absent in lists.</param>
public sealed record NotificationResponse(
    Guid Id,
    string TenantId,
    string Recipient,
    string EventKey,
    string ReferenceKey,
    NotificationKind Kind,
    NotificationStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    string? TemplateName,
    string? TemplateLanguage,
    IReadOnlyList<string> TemplateParameters,
    string? Text,
    string? ProviderMessageId,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DeliveryStatus? DeliveryStatus = null,
    Guid? FallbackNotificationId = null,
    IReadOnlyList<DeliveryReceiptResponse>? Receipts = null);

/// <summary>One status event the provider reported for the message, in the order it happened.</summary>
/// <param name="Status">The provider status: <c>sent</c>, <c>delivered</c>, <c>read</c>, <c>failed</c>, or a word this service does not interpret.</param>
public sealed record DeliveryReceiptResponse(
    string ProviderMessageId,
    string Status,
    DateTimeOffset OccurredAt,
    int? ErrorCode,
    string? ErrorTitle);

public sealed record NotificationPageResponse(IReadOnlyList<NotificationResponse> Items, int Page, int PageSize, int Total);

public sealed record RetryNotificationResponse(Guid Id, NotificationStatus Status);

/// <summary>A booking as the booking system saw it when it raised the event. The tenant is taken from the API key.</summary>
public sealed record BookingSnapshotRequest
{
    public required string BookingId { get; init; }

    public required string Reference { get; init; }

    public required string CustomerFirstName { get; init; }

    /// <summary>Country calling code without the plus sign, for example <c>44</c>.</summary>
    public required string PhoneCountryCallingCode { get; init; }

    public required string PhoneNationalNumber { get; init; }

    public required BookingType BookingType { get; init; }

    public required BookingStatus Status { get; init; }

    public required BookingDetailsInput Details { get; init; }

    public required string CompanyName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? PreferredLanguage { get; init; }

    public bool WhatsappUpdatesEnabled { get; init; }
}

public sealed record EventAcceptedResponse(string EventKey, string ReferenceKey);

/// <summary>Simulates an inbound customer message so the 24 hour service window opens.</summary>
public sealed record InboundMessageRequest
{
    public required string TenantId { get; init; }

    public required string Recipient { get; init; }

    public string? DefaultRegion { get; init; }

    /// <summary>When the message arrived. Defaults to now.</summary>
    public DateTimeOffset? ReceivedAt { get; init; }
}

public sealed record InboundMessageResponse(string TenantId, string Recipient, DateTimeOffset WindowOpenUntil);
