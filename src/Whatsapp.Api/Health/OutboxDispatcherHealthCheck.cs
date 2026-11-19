using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Whatsapp.Outbox;

namespace Whatsapp.Api.Health;

/// <summary>Unhealthy when the dispatcher failed its last cycle or has stopped polling.</summary>
internal sealed class OutboxDispatcherHealthCheck(
    OutboxDispatcherStatus status,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    private static readonly TimeSpan MinimumStaleAfter = TimeSpan.FromSeconds(30);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.DispatcherEnabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("The dispatcher is disabled."));
        }

        if (status.LastCycleAt is not { } lastCycle)
        {
            return Task.FromResult(HealthCheckResult.Healthy("The dispatcher has not completed a cycle yet."));
        }

        if (status.LastCycleFailed)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The last dispatch cycle failed."));
        }

        var staleAfter = TimeSpan.FromTicks(Math.Max(MinimumStaleAfter.Ticks, settings.PollInterval.Ticks * 5));
        return Task.FromResult(timeProvider.GetUtcNow() - lastCycle > staleAfter
            ? HealthCheckResult.Unhealthy("The dispatcher has stopped polling.")
            : HealthCheckResult.Healthy("The dispatcher is polling."));
    }
}
