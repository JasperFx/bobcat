using Bobcat.Ui.Contracts;
using Bobcat.Ui.Runs;

namespace Bobcat.Ui.Runners;

/// <summary>
/// Pushes the runner list to the browser whenever it changes (bobcat#784), through the SignalR batch
/// accumulator as a <see cref="RunnersChanged"/> notice carrying the whole list — the same shape
/// <c>GET /api/runners</c> serves — so the board, a run page and a runner's own page stay current
/// without the 5-second poll they used to run.
/// </summary>
/// <remarks>
/// Two things move a runner's view: the runner registry itself (a registration, a stream opening or
/// closing, a status report, a command queued, sent or answered — <see cref="RunnerRegistry.Changed"/>),
/// and a run starting under a command id (the command's <c>runId</c> link, which only the run
/// registry knows — the ingestion endpoint calls <see cref="Publish"/> for that). Both reduce to one
/// snapshot, and the accumulator keeps only the latest per flush.
/// </remarks>
public sealed class RunnerNotices : IHostedService
{
    private readonly RunnerRegistry _runners;
    private readonly MonitorRunRegistry _runs;
    private readonly SignalRBatchAccumulator _accumulator;

    public RunnerNotices(RunnerRegistry runners, MonitorRunRegistry runs, SignalRBatchAccumulator accumulator)
    {
        _runners = runners;
        _runs = runs;
        _accumulator = accumulator;
    }

    /// <summary>Queue the current runner list for the next flush.</summary>
    public void Publish() => _accumulator.Notify(new RunnersChanged(RunnerEndpoints.AllRunners(_runners, _runs)));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runners.Changed += Publish;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _runners.Changed -= Publish;
        return Task.CompletedTask;
    }
}
