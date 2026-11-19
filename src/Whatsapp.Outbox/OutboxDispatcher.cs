using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whatsapp.Outbox;

/// <summary>Heartbeat of the dispatcher, read by the readiness health check.</summary>
public sealed class OutboxDispatcherStatus
{
    private long _lastCycleTicks;
    private int _lastCycleFailed;

    public DateTimeOffset? LastCycleAt => _lastCycleTicks == 0
        ? null
        : new DateTimeOffset(Interlocked.Read(ref _lastCycleTicks), TimeSpan.Zero);

    public bool LastCycleFailed => Volatile.Read(ref _lastCycleFailed) == 1;

    internal void RecordCycle(DateTimeOffset at, bool failed)
    {
        Interlocked.Exchange(ref _lastCycleTicks, at.UtcTicks);
        Volatile.Write(ref _lastCycleFailed, failed ? 1 : 0);
    }
}

/// <summary>Background loop that repeatedly runs <see cref="OutboxProcessor"/>. It never lets an error stop the host.</summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    OutboxDispatcherStatus status,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.DispatcherEnabled)
        {
            logger.LogInformation("Outbox dispatcher is disabled");
            return;
        }

        logger.LogInformation(
            "Outbox dispatcher started (batch {BatchSize}, concurrency {Concurrency}, max attempts {MaxAttempts})",
            settings.BatchSize,
            settings.Concurrency,
            settings.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                // A scope per cycle gives every cycle a fresh HTTP client handler lifetime scope.
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                processed = await processor.ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
                status.RecordCycle(timeProvider.GetUtcNow(), failed: false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch cycle failed; it will be retried");
                status.RecordCycle(timeProvider.GetUtcNow(), failed: true);
            }

            if (processed >= settings.BatchSize)
            {
                continue;
            }

            try
            {
                await Task.Delay(settings.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Outbox dispatcher stopped");
    }
}
