using Bobcat.Monitoring;
using Bobcat.Resilience;

namespace Bobcat.Supervisor;

/// <summary>
/// The supervisor's half of monitor publishing: it owns the run bracket (RunStarted,
/// heartbeats, RunFinished) for the whole supervised run, and hands every worker the
/// environment pair that makes them participants — <c>BOBCAT_RUN_ID</c> so their scenario
/// and step streams land on this run, and <c>BOBCAT_RUN_OWNER</c> so their own
/// <see cref="MonitorPublishingObserver"/> suppresses its bracket. Without the split, every
/// worker was its own dashboard card, and with a shared id alone the first worker to finish
/// would have marked the whole run finished with its partial counts.
/// </summary>
internal sealed class SupervisorRunPublisher : ISupervisorObserver, IAsyncDisposable
{
    private static readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly IMonitorEventSink _sink;
    private readonly MonitorRunInfo _info;
    private readonly IAsyncDisposable? _ownedPublisher;
    private Timer? _heartbeat;

    private SupervisorRunPublisher(IMonitorEventSink sink, MonitorRunInfo info, IAsyncDisposable? ownedPublisher)
    {
        _sink = sink;
        _info = info;
        _ownedPublisher = ownedPublisher;

        WorkerEnvironment = new Dictionary<string, string>
        {
            [MonitorRunInfo.RunIdVariable] = info.RunId.ToString(),
            [MonitorRunInfo.RunOwnerVariable] = "supervisor"
        };
    }

    /// <summary>
    /// Null when the monitor is absent or disabled — the run then proceeds exactly as before,
    /// and workers get no grouping environment (each publishes its own run, the old behavior,
    /// rather than a grouped run whose bracket nobody would post).
    /// </summary>
    public static async Task<SupervisorRunPublisher?> TryStart(IMonitorEventSink? sink, string suite)
    {
        IAsyncDisposable? owned = null;
        if (sink is null)
        {
            var publisher = await MonitorPublisher.TryConnect();
            if (publisher is null) return null;

            sink = publisher;
            owned = publisher;
        }

        // The supervisor IS this run's owner, whatever its own environment says — Discover
        // still honours an externally-set BOBCAT_RUN_ID, so a CI wrapper can pin the identity.
        var info = MonitorRunInfo.Discover("supervised") with
        {
            Suite = suite,
            HasExternalOwner = false
        };

        return new SupervisorRunPublisher(sink, info, owned);
    }

    public Guid RunId => _info.RunId;

    /// <summary>The pair every worker launch carries, via <see cref="WorkerLaunchContext.Environment"/>.</summary>
    public IReadOnlyDictionary<string, string> WorkerEnvironment { get; }

    public void RunStarted(int? totalTests)
    {
        _sink.Post(new RunStarted(
            _info.RunId, _info.Suite, _info.Repository, _info.Branch, _info.Mode,
            DateTimeOffset.UtcNow, totalTests, _info.Tag));

        _heartbeat = new Timer(
            _ => _sink.Post(new RunHeartbeat(_info.RunId, DateTimeOffset.UtcNow)),
            null, heartbeatInterval, heartbeatInterval);
    }

    public void RunFinished(SupervisorResults results)
    {
        stopHeartbeat();

        _sink.Post(new RunFinished(
            _info.RunId,
            results.ExitCode,
            Passed: results.CleanPasses.Count,
            // Indeterminate is NOT folded into Failed: "we don't know what happened" and
            // "it failed" are different situations — same reasoning as exit code 2 vs 1.
            Failed: results.Failed.Count(t => !t.IsIndeterminate),
            PassedOnRetry: results.PassedOnRetry.Count,
            Indeterminate: results.Indeterminate.Count,
            DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The retry topology, which only the supervisor knows — issue #84.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things reach the dashboard that could not before. The obvious one is the policy's
    /// verdict: a supervised retry used to appear as a scenario that simply ran again, with
    /// neither the disposition nor the reason anywhere on the wire.
    /// </para>
    /// <para>
    /// The load-bearing one is the <em>attempt number</em>. A worker counts its attempts from one
    /// — <see cref="MonitorPublishingObserver"/>'s tracking is per <c>BobcatRunner</c>, and the
    /// MTP host builds a fresh runner for every run request, so even a same-process retry
    /// restarts at one. The supervisor holds the only true count, and a projection that folds
    /// this event knows the next <c>ScenarioStarted</c> is that attempt however the worker
    /// numbered it. Without it a supervised retry overwrote its own previous attempt and
    /// CTRF's <c>retryAttempts[]</c> worked for in-process retries only.
    /// </para>
    /// </remarks>
    public void RetryScheduled(string uid, int nextAttempt, Disposition disposition)
        => _sink.Post(new RetryScheduled(
            _info.RunId, uid, nextAttempt, disposition.Kind.ToString(), disposition.Reason));

    /// <summary>
    /// Same rationale as MonitorPublishingObserver: plain Timer.Dispose() does not wait for an
    /// in-flight callback, so a heartbeat could post after RunFinished on a busy box.
    /// </summary>
    private void stopHeartbeat()
    {
        var timer = _heartbeat;
        _heartbeat = null;
        if (timer == null) return;

        using var drained = new ManualResetEvent(false);
        if (timer.Dispose(drained))
        {
            drained.WaitOne(TimeSpan.FromSeconds(1));
        }
    }

    public async ValueTask DisposeAsync()
    {
        stopHeartbeat();

        if (_ownedPublisher != null) await _ownedPublisher.DisposeAsync();
    }
}
