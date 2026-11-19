namespace Whatsapp.Abstractions;

/// <summary>How far a message got on its way to the recipient, as told by the provider's status webhooks.</summary>
public enum DeliveryStatus
{
    /// <summary>The provider accepted the message and sent it on.</summary>
    Sent = 1,

    /// <summary>The message reached the recipient's device.</summary>
    Delivered = 2,

    /// <summary>The recipient opened the message.</summary>
    Read = 3,

    /// <summary>The provider gave up on the message. Terminal: nothing that arrives later undoes it.</summary>
    Failed = 4,
}

/// <summary>The status words of the provider, in the lower case this service stores.</summary>
public static class ReceiptStatuses
{
    public const string Sent = "sent";
    public const string Delivered = "delivered";
    public const string Read = "read";
    public const string Failed = "failed";
}

/// <summary>
/// Reduces the receipts of a message to one <see cref="DeliveryStatus"/>. The result depends only on which statuses
/// were seen and never on the order they arrived in: <c>sent &lt; delivered &lt; read</c> is a monotonic ladder, and
/// <c>failed</c> is terminal, so a late <c>sent</c> or <c>delivered</c> can neither lower nor overwrite it.
/// </summary>
public static class DeliveryStatusDerivation
{
    /// <returns>The derived status, or null when no receipt carries a status this service knows.</returns>
    public static DeliveryStatus? Derive(IEnumerable<string> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        DeliveryStatus? derived = null;
        foreach (var status in statuses)
        {
            var known = Parse(status);
            if (known is null)
            {
                continue;
            }

            if (known == DeliveryStatus.Failed)
            {
                return DeliveryStatus.Failed;
            }

            if (derived is null || known > derived)
            {
                derived = known;
            }
        }

        return derived;
    }

    /// <returns>The status for a provider word, or null for one this service does not know (for example <c>deleted</c>).</returns>
    public static DeliveryStatus? Parse(string? status) => status switch
    {
        ReceiptStatuses.Sent => DeliveryStatus.Sent,
        ReceiptStatuses.Delivered => DeliveryStatus.Delivered,
        ReceiptStatuses.Read => DeliveryStatus.Read,
        ReceiptStatuses.Failed => DeliveryStatus.Failed,
        _ => null,
    };
}
