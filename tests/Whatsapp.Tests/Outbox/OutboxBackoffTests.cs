using Whatsapp.Outbox;

namespace Whatsapp.Tests.Outbox;

public class OutboxBackoffTests
{
    private static readonly OutboxOptions Options = new()
    {
        RetryBaseDelay = TimeSpan.FromSeconds(30),
        RetryMaxDelay = TimeSpan.FromMinutes(10),
    };

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 600)]
    [InlineData(40, 600)]
    public void The_full_delay_doubles_per_attempt_and_is_capped(int attempt, int expectedSeconds)
    {
        var backoff = new ExponentialBackoff(Options, () => 1.0);

        backoff.NextDelay(attempt, null).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(0.0, 15)]
    [InlineData(0.5, 22.5)]
    [InlineData(1.0, 30)]
    public void Jitter_spreads_the_delay_between_half_and_full(double random, double expectedSeconds)
    {
        var backoff = new ExponentialBackoff(Options, () => random);

        backoff.NextDelay(1, null).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Out_of_range_random_values_are_clamped()
    {
        new ExponentialBackoff(Options, () => 7.0).NextDelay(1, null).ShouldBe(TimeSpan.FromSeconds(30));
        new ExponentialBackoff(Options, () => -3.0).NextDelay(1, null).ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void A_longer_retry_after_wins_and_a_shorter_one_is_ignored()
    {
        var backoff = new ExponentialBackoff(Options, () => 1.0);

        backoff.NextDelay(1, TimeSpan.FromMinutes(5)).ShouldBe(TimeSpan.FromMinutes(5));
        backoff.NextDelay(1, TimeSpan.FromSeconds(5)).ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Real_randomness_stays_inside_the_documented_bounds()
    {
        var backoff = new ExponentialBackoff(Options, Random.Shared.NextDouble);

        for (var i = 0; i < 200; i++)
        {
            var delay = backoff.NextDelay(3, null);
            delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));
            delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(120));
        }
    }
}
