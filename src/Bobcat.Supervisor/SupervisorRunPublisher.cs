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

    /// <summary>
    /// When each in-flight test was last announced in-progress, so a verdict can be given a
    /// duration (issue #195). The supervisor's own clock, not the worker's: it is the only one
    /// both ends of the transition are measured on. A test whose start we never saw simply has
    /// no duration — unmeasured is never zero.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _startedAt = new(StringComparer.Ordinal);

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

    // The rest of the topology — lanes, recycles, worker deaths (issue #84). SupervisorResults
    // reports all of it at the end; these put it on the wire as it happens, which is when a
    // person watching a long run wants it. The timestamps are the supervisor's clock, so a
    // dashboard can order them against each other without trusting arrival order.

    public void LaneStarted(int lane, IReadOnlyList<string> uids)
        => _sink.Post(new LaneStarted(_info.RunId, lane, uids.ToArray(), DateTimeOffset.UtcNow));

    public void LaneFinished(int lane, WorkerRunResult result)
        => _sink.Post(new LaneFinished(
            _info.RunId,
            lane,
            // What the worker actually reported. A crashed worker's result is padded with
            // Indeterminate for every test it never answered for, and those are not outcomes.
            Outcomes: result.Outcomes.Count(o => o.State != WorkerTestState.Indeterminate),
            result.Crashed,
            DateTimeOffset.UtcNow));

    public void ResourceRecycled(string name)
        => _sink.Post(new ResourceRecycled(_info.RunId, name, DateTimeOffset.UtcNow));

    public void WorkerFaulted(WorkerFault fault)
        => _sink.Post(new WorkerFaulted(
            _info.RunId, fault.Lane, fault.Description, fault.ExitCode, fault.StandardError, DateTimeOffset.UtcNow));

    // The observability cluster's live surfaces (issues #145/#146/#148/#149) put on the wire.
    // Same lane rule as WorkerFaulted throughout: a one-test isolated/recycled process (and
    // discovery) reports a null lane, so a dashboard never invents a slot for it.

    public void WorkerStarted(WorkerLaunchContext worker)
    {
        // Discovery launches BEFORE the run bracket opens — RunStarted carries the post-filter
        // total, which discovery exists to produce — so announcing it would put an event on
        // the wire ahead of run_started, the one ordering every consumer may assume. Code
        // observers still hear it; the wire does not.
        if (worker.Purpose == WorkerPurpose.Discovery) return;

        _sink.Post(new WorkerStarted(
            _info.RunId, laneOf(worker), worker.Purpose.ToString(), worker.ProcessId, DateTimeOffset.UtcNow));
    }

    public void TestStalled(WorkerLaunchContext worker, string uid, string displayName, TimeSpan inFlight)
        => _sink.Post(new TestStalled(
            _info.RunId, uid, displayName, (long)inFlight.TotalMilliseconds,
            laneOf(worker), worker.ProcessId, DateTimeOffset.UtcNow));

    /// <summary>
    /// The live per-test stream, forwarded (issue #195). This is the ONLY per-test progress a
    /// run whose workers are not themselves Bobcat runners ever produces: a plain xUnit worker
    /// has no <see cref="MonitorPublishingObserver"/>, so without this a supervised suite of
    /// 1600 tests registered on the dashboard and then sat at 0 finished for its whole
    /// five-minute run — visually indistinguishable from a wedged one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately not <c>scenario_started</c>/<c>scenario_finished</c>.</strong> Those
    /// carry spec identity — <c>{Feature}/{Scenario}</c>, the string a design-time
    /// <c>SpecificationDescriptor</c> joins on — and a worker's own publisher owns them. Feeding
    /// them an xUnit method uid would widen that meaning for every consumer of the join. These
    /// are a separate, lower-fidelity pair, and the console's fold lets a worker's own scenario
    /// stream win for any uid it has touched — so a Bobcat worker publishing beside its
    /// supervisor is not double-reported, in either arrival order.
    /// </para>
    /// <para>
    /// Only a classified terminal state is announced as finished. MTP streams other node
    /// changes through the same channel (a re-announced discovery, for one), and
    /// <see cref="MtpWorkerClient"/> reports anything it cannot classify as
    /// <see cref="WorkerTestState.Indeterminate"/> — which is absence of evidence, not a
    /// verdict, and must never move a progress bar.
    /// </para>
    /// <para>
    /// A supervised Bobcat suite therefore puts both streams on the wire and the console keeps
    /// one. That redundancy is deliberate: the alternative is the supervisor knowing which of
    /// its workers publishes, which it cannot learn without a new marker the worker has to
    /// carry — and a marker only new workers would set, leaving the fold to handle the rest
    /// anyway. Two extra events per test against a publisher that batches and drops under
    /// backpressure was the cheaper side of that trade.
    /// </para>
    /// </remarks>
    public void TestUpdated(WorkerLaunchContext worker, WorkerTestUpdate update)
    {
        var at = DateTimeOffset.UtcNow;

        if (update.InProgress)
        {
            lock (_startedAt) _startedAt[update.Uid] = at;
            _sink.Post(new TestStarted(_info.RunId, update.Uid, update.DisplayName, laneOf(worker), at));
            return;
        }

        if (update.State is not { } state || state == WorkerTestState.Indeterminate) return;

        long? durationMs;
        lock (_startedAt)
        {
            durationMs = _startedAt.Remove(update.Uid, out var started)
                ? (long)(at - started).TotalMilliseconds
                : null;
        }

        _sink.Post(new TestFinished(
            _info.RunId, update.Uid, update.DisplayName, state.ToString(), durationMs, laneOf(worker), at));
    }

    public void Heartbeat(SupervisorHeartbeat heartbeat)
        => _sink.Post(new RunProgress(
            _info.RunId,
            (long)heartbeat.Elapsed.TotalMilliseconds,
            heartbeat.Completed,
            heartbeat.Total,
            heartbeat.InFlight.Count,
            heartbeat.LongestRunning?.Uid,
            heartbeat.LongestRunning?.DisplayName,
            heartbeat.LongestRunning is { } longest ? (long)longest.InFlight.TotalMilliseconds : null,
            heartbeat.PeakWorkerRssBytes,
            DateTimeOffset.UtcNow));

    private static int? laneOf(WorkerLaunchContext worker)
        => worker.Purpose == WorkerPurpose.Lane ? worker.Lane : null;

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
