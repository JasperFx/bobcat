using Bobcat.Ui.Contracts;

namespace Bobcat.Ui.Runs;

/// <summary>
/// Orphans the runs nobody is publishing to any more (bobcat#784). The registry rehydrated a run
/// with no terminal event as orphaned, but a publisher that died while the console was UP left its
/// run live forever: a progress bar, not ejectable, never evicted. This asks the registry to sweep
/// on a cadence a fraction of <see cref="MonitorRunRegistry.OrphanAfter"/>, and pushes each newly
/// orphaned run to the browser as a <see cref="RunOrphaned"/> notice through the batch accumulator,
/// so the card changes without a refresh. Inert when the registry's interval is zero or negative.
/// </summary>
public class OrphanSweepService : BackgroundService
{
    private readonly MonitorRunRegistry _registry;
    private readonly SignalRBatchAccumulator _accumulator;
    private readonly TimeProvider _time;
    private readonly ILogger<OrphanSweepService> _logger;

    public OrphanSweepService(
        MonitorRunRegistry registry, SignalRBatchAccumulator accumulator, ILogger<OrphanSweepService> logger,
        TimeProvider? time = null)
    {
        _registry = registry;
        _accumulator = accumulator;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// How often the sweep runs: a third of the interval, so a run is orphaned no later than a third
    /// past it, and never more often than once a second.
    /// </summary>
    public static TimeSpan PeriodFor(TimeSpan orphanAfter)
        => orphanAfter > TimeSpan.FromSeconds(3) ? orphanAfter / 3 : TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_registry.OrphanAfter <= TimeSpan.Zero) return;

        using var timer = new PeriodicTimer(PeriodFor(_registry.OrphanAfter), _time);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                SweepOnce();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orphan sweep failed");
            }
        }
    }

    /// <summary>One sweep, as the timer runs it: public so a test can drive it without the clock.</summary>
    public void SweepOnce()
    {
        foreach (var orphan in _registry.SweepOrphans())
        {
            _logger.LogInformation(
                "Run {RunId} orphaned: nothing heard from its publisher since {LastEventAt}", orphan.RunId, orphan.LastEventAt);
            _accumulator.Notify(new RunOrphaned(orphan.RunId, orphan.LastEventAt));
        }
    }
}
