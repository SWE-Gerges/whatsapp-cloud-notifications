namespace Whatsapp.Outbox;

/// <summary>Decides how long to wait before the next durable retry of a row.</summary>
public interface IOutboxBackoff
{
    /// <param name="attempt">Number of attempts already made (1 after the first failure).</param>
    /// <param name="retryAfter">A delay requested by the provider; the result is never shorter.</param>
    TimeSpan NextDelay(int attempt, TimeSpan? retryAfter);
}

/// <summary>
/// Exponential backoff (base, 2x base, 4x base, ...) capped at the maximum delay, with "equal jitter":
/// the result lies between half and the full computed delay so that rows failing together do not retry together.
/// </summary>
public sealed class ExponentialBackoff(OutboxOptions options, Func<double> nextRandom) : IOutboxBackoff
{
    public TimeSpan NextDelay(int attempt, TimeSpan? retryAfter)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var seconds = Math.Min(
            options.RetryMaxDelay.TotalSeconds,
            options.RetryBaseDelay.TotalSeconds * Math.Pow(2, exponent));

        var jittered = TimeSpan.FromSeconds(seconds * (0.5 + (0.5 * Math.Clamp(nextRandom(), 0d, 1d))));
        return retryAfter is { } requested && requested > jittered ? requested : jittered;
    }
}
