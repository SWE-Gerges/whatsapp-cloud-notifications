using System.Net;

namespace Whatsapp.Abstractions;

/// <summary>A failed call to the Cloud API, classified as transient or permanent.</summary>
public sealed class WhatsappApiException : Exception
{
    public WhatsappApiException(
        WhatsappErrorKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        GraphError? graphError = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null,
        bool outcomeUnknown = false)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        GraphError = graphError;
        RetryAfter = retryAfter;
        OutcomeUnknown = outcomeUnknown;
    }

    public WhatsappErrorKind Kind { get; }

    public bool IsTransient => Kind.IsTransient();

    public HttpStatusCode? StatusCode { get; }

    /// <summary>The parsed Graph error envelope, when the provider sent one.</summary>
    public GraphError? GraphError { get; }

    /// <summary>The delay requested by the provider through the Retry-After header.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// True when the request may have reached the provider and the message may therefore have been accepted: a read
    /// timeout, a connection reset after the request was written, or a success response that could not be understood.
    /// Such a failure must not be retried blindly, because that can deliver the message twice. It is false for a
    /// response that carries an error body and for a failure that provably happened before the request left (DNS,
    /// connect, TLS handshake).
    /// </summary>
    public bool OutcomeUnknown { get; }
}

/// <summary>The error object of a Graph API failure response.</summary>
public sealed record GraphError(
    string? Message,
    string? Type,
    int? Code,
    int? Subcode,
    string? FbTraceId);
