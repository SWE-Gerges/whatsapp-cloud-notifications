namespace Whatsapp.Abstractions;

/// <summary>Lifecycle of an outbox row.</summary>
public enum NotificationStatus
{
    /// <summary>Waiting to be sent, or waiting for the next retry.</summary>
    Pending = 0,

    /// <summary>Claimed by a dispatcher worker under a time-limited lease.</summary>
    Sending = 1,

    /// <summary>Accepted by the Cloud API.</summary>
    Sent = 2,

    /// <summary>
    /// Rejected locally before reaching the provider (unknown tenant, invalid template data), or accepted by the provider
    /// and then reported undeliverable by a <c>failed</c> receipt. Only a manual retry sends it again.
    /// </summary>
    Failed = 3,

    /// <summary>Intentionally not sent (disabled tenant, closed service window without fallback).</summary>
    Skipped = 4,

    /// <summary>Attempted and given up: permanent provider error, retries exhausted, or an unconfirmed send that was never confirmed.</summary>
    DeadLettered = 5,

    /// <summary>
    /// The request may have reached the provider but nothing confirmed it: a worker died or the connection broke after the
    /// request was written. The row waits for a receipt that echoes its id; without one, the reconciler applies the configured
    /// policy once the grace period has passed (default: dead-letter, never send again automatically).
    /// </summary>
    Unconfirmed = 6,
}
