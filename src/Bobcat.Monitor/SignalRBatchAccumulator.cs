using System.Collections.Concurrent;
using Bobcat.Monitor.Contracts;
using Wolverine;
using Wolverine.Util;

namespace Bobcat.Monitor;

/// <summary>
/// Coalesces ingested monitor events emitted within a short window into a single
/// <see cref="BatchedWebSocketPayload"/> that ships to SignalR as one frame, instead of one
/// frame per event — CritterWatch's SignalRBatchAccumulator pattern. The ingestion endpoint
/// enqueues here (after the registry fold — the registry must never lag the browser) and the
/// periodic flush publishes the envelope through the one SignalR publish rule in Program.cs.
/// </summary>
/// <remarks>
/// A step-heavy suite emits StepStarted/StepFinished pairs at whatever rate the specs run;
/// several publishers can stream at once. 100&#160;ms is short enough to feel live (the
/// frontend rAF-batches downstream of this anyway) and long enough to fold a burst of step
/// events into one frame. Events are relayed in arrival order within and across batches —
/// one queue, one drain.
/// </remarks>
public class SignalRBatchAccumulator : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SignalRBatchAccumulator> _logger;
    private readonly ConcurrentQueue<MonitorEvent> _queue = new();

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public SignalRBatchAccumulator(IServiceProvider services, ILogger<SignalRBatchAccumulator> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void Enqueue(IEnumerable<MonitorEvent> events)
    {
        foreach (var @event in events) _queue.Enqueue(@event);
    }

    /// <summary>
    /// Drain the queue once into a single envelope; null when nothing is pending (saves a
    /// no-op SignalR send). Public so tests can drive the accumulator deterministically
    /// without spinning the background timer.
    /// </summary>
    public BatchedWebSocketPayload? DrainOnce()
    {
        if (_queue.IsEmpty) return null;

        var items = new List<BatchedWebSocketItem>(capacity: _queue.Count);
        while (_queue.TryDequeue(out var @event))
        {
            items.Add(new BatchedWebSocketItem(@event.GetType().ToMessageTypeName(), @event));
        }

        return items.Count > 0 ? new BatchedWebSocketPayload(items.ToArray()) : null;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var batch = DrainOnce();
        if (batch is null) return;

        using var scope = _services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(batch);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(FlushInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                await FlushAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush the SignalR batch");
            }
        }

        // Final drain on shutdown so events enqueued just before stop aren't silently dropped
        // — the publisher-side mirror of the heartbeat-drain guarantee.
        try { await FlushAsync(CancellationToken.None); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final SignalR batch flush failed during shutdown");
        }
    }
}
