using System.ComponentModel.DataAnnotations;

namespace Whatsapp.CloudApi;

/// <summary>Settings of the HTTP client and its in-process retry pipeline (not tenant specific).</summary>
public sealed class WhatsappClientOptions
{
    public const string SectionName = "Whatsapp:Client";

    /// <summary>Extra attempts after the first one for transient failures. Durable retries live in the outbox.</summary>
    [Range(0, 5)]
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Base delay of the exponential, jittered backoff.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// A <c>Retry-After</c> longer than this is not waited for inside the request. The exception carries the
    /// value and the outbox schedules the next attempt instead.
    /// </summary>
    public TimeSpan MaxInlineRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout of a single HTTP attempt.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
