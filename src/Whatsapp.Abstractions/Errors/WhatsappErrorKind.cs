namespace Whatsapp.Abstractions;

/// <summary>Typed reason a send failed. Drives the retry decision.</summary>
public enum WhatsappErrorKind
{
    /// <summary>
    /// Connection level failure. Transient. Whether the request may have left is told by
    /// <see cref="WhatsappApiException.OutcomeUnknown"/>: DNS, connect and TLS failures provably did not send it, a reset
    /// after the request was written may have.
    /// </summary>
    Network = 0,

    /// <summary>No response within the request timeout. Transient, but the request may have been accepted, so the outcome is unknown.</summary>
    Timeout = 1,

    /// <summary>Throttled by the provider (HTTP 429 or a rate-limit error code). Transient.</summary>
    RateLimited = 2,

    /// <summary>HTTP 5xx or a provider side temporary error code. Transient.</summary>
    ServerError = 3,

    /// <summary>The recipient is invalid, unreachable or not allowed. Permanent.</summary>
    InvalidRecipient = 10,

    /// <summary>The template does not exist, is paused or not approved. Permanent.</summary>
    TemplateUnavailable = 11,

    /// <summary>Template parameters do not match the approved template. Permanent.</summary>
    ParameterMismatch = 12,

    /// <summary>The token is missing, invalid or lacks permission. Permanent.</summary>
    Authentication = 13,

    /// <summary>The access token has expired. Permanent until the token is rotated.</summary>
    TokenExpired = 14,

    /// <summary>Free-form text sent outside the 24 hour service window. Permanent.</summary>
    OutsideServiceWindow = 15,

    /// <summary>Any other rejected request. Permanent.</summary>
    InvalidRequest = 16,

    /// <summary>A 2xx answer that could not be understood. Not retried, because the message may have been accepted.</summary>
    InvalidResponse = 17,
}

public static class WhatsappErrorKindExtensions
{
    public static bool IsTransient(this WhatsappErrorKind kind) =>
        kind is WhatsappErrorKind.Network
            or WhatsappErrorKind.Timeout
            or WhatsappErrorKind.RateLimited
            or WhatsappErrorKind.ServerError;
}
