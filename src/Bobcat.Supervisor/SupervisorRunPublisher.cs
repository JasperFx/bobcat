using Bobcat.Monitoring;
using Bobcat.Resilience;

namespace Bobcat.Supervisor;

/// <summary>
/// The supervisor's half of monitor publishing: it owns the run bracket (RunStarted,
/// heartbeats, RunFinished) for the whole supervised run, and hands every worker the
/// environment pair that makes them participants — <c>BOBCAT_RUN_ID</c> so their scenario
/// and step streams land on this run, and <c>BOBCAT_RUN_OWNER</c> so their own
/// run bracket (<see cref="MarkerStepRun"/>) is suppressed. Without the split, every
/// worker was its own dashboard card, and with a shared id alone the first worker to finish
/// would have marked the whole run finished with its partial counts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It also keeps the run's live counts and publishes them as <c>run_progress</c></strong>
/// (bobcat#767, for stoat#157's progress bar). The counts are folded from the same observer
/// callbacks the wire already carries — a worker's live per-test stream for in-flight and
/// verdicts, <see cref="AttemptRecorded"/> for the authoritative verdict, and
/// <see cref="RetryScheduled"/> for what will run again — because the supervisor records a lane's
/// attempts only when the lane returns, and a bar that moves once per lane is not live.
/// </para>
/// <para>
/// <strong>The cadence is bounded</strong>: the first change posts at once, a further change posts
/// when at least <see cref="ProgressInterval"/> has passed since the last post and is otherwise
/// held for one trailing post at the end of that interval (so the final state always lands), and a
/// change in the failed count posts immediately whatever the clock says — a red bar should not wait
/// a second to go red. The opt-in <see cref="Heartbeat"/> beat posts too, and is the only path that
/// learns peak worker RSS, which is carried forward on every later post because "highest so far"
/// only climbs. Every post measures <c>ElapsedMs</c> from <c>run_started</c> on the one clock, so a
/// reader's latest-wins fold never sees two origins.
/// </para>
/// </remarks>
internal sealed class SupervisorRunPublisher : ISupervisorObserver, IAsyncDisposable
{
    private static readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>The least time between two change-driven <c>run_progress</c> posts (bobcat#767).</summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    private readonly IMonitorEventSink _sink;
    private readonly MonitorRunInfo _info;
    private readonly IAsyncDisposable? _ownedPublisher;
    private readonly TimeProvider _time;

    /// <summary>
    /// Uid → <c>{Feature}/{Scenario}</c> for the tests the suite's own spec manifest names
    /// (issue #515), bound once at discovery. Empty for a suite that writes no manifest, which
    /// is every run of foreign tests — those simply carry no identity.
    /// </summary>
    private IReadOnlyDictionary<string, string> _specIds = new Dictionary<string, string>(StringComparer.Ordinal);

    private Timer? _heartbeat;

    // --- The live counts behind run_progress (bobcat#767). Written from worker I/O threads and
    // the supervisor's own thread, read by the trailing timer, so everything is behind one gate.

    private readonly object _progress = new();

    /// <summary>
    /// Each in-flight test: when it was last announced in-progress, so a verdict can be given a
    /// duration (issue #195) — the supervisor's own wall clock, the only one both ends of the
    /// transition are measured on; the <see cref="TimeProvider"/> stamp the longest-running clause
    /// reads; and which worker has it. A test whose start we never saw simply has no duration —
    /// unmeasured is never zero.
    /// </summary>
    private readonly Dictionary<string, InFlightEntry> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// The latest thing known about each test that has had a verdict: keyed, never counted, so a
    /// retried test is one test and <c>completed</c> never exceeds <c>total</c>.
    /// </summary>
    private readonly Dictionary<string, ProgressVerdict> _verdicts = new(StringComparer.Ordinal);

    private readonly HashSet<string> _retried = new(StringComparer.Ordinal);

    private int? _total;
    private long _runStartedAt;
    private bool _started;
    private bool _finished;
    private long? _lastProgressAt;
    private ITimer? _trailing;
    private long? _peakWorkerRssBytes;

    private sealed record InFlightEntry(string DisplayName, string Worker, DateTimeOffset StartedAt, long StartedStamp);

    private enum ProgressVerdict
    {
        Passed,
        Failed,
        Skipped,
        Indeterminate,

        /// <summary>A verdict the supervisor has decided to retry: not finished, and not failed.</summary>
        PendingRetry
    }

    private SupervisorRunPublisher(
        IMonitorEventSink sink, MonitorRunInfo info, IAsyncDisposable? ownedPublisher, TimeProvider time)
    {
        _sink = sink;
        _info = info;
        _ownedPublisher = ownedPublisher;
        _time = time;
        _runStartedAt = time.GetTimestamp();

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
    /// <param name="time">
    /// The supervisor's clock (<see cref="Supervisor.Time"/>), which paces the progress cadence and
    /// measures <c>run_progress.elapsedMs</c>; null is the system clock.
    /// </param>
    public static async Task<SupervisorRunPublisher?> TryStart(
        IMonitorEventSink? sink, string suite, TimeProvider? time = null)
    {
        IAsyncDisposable? owned = null;
        if (sink is null)
        {
            // Every target BOBCAT_MONITOR_URL names (bobcat#530). The workers inherit the variable
            // unchanged through WorkerLaunchContext.Environment, so their own streams fan out too.
            var publisher = await MonitorTargets.TryConnect();
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

        return new SupervisorRunPublisher(sink, info, owned, time ?? TimeProvider.System);
    }

    public Guid RunId => _info.RunId;

    /// <summary>The pair every worker launch carries, via <see cref="WorkerLaunchContext.Environment"/>.</summary>
    public IReadOnlyDictionary<string, string> WorkerEnvironment { get; }

    /// <summary>
    /// Give the publisher the uid → spec identity map discovery produced, so the forwarded
    /// per-test stream can name the specification each test is (issue #515). Called before
    /// <see cref="RunStarted"/>, since nothing is forwarded until a lane runs.
    /// </summary>
    public void BindSpecIdentities(IReadOnlyDictionary<string, string> specIds) => _specIds = specIds;

    public void RunStarted(int? totalTests)
    {
        lock (_progress)
        {
            _total = totalTests;
            _runStartedAt = _time.GetTimestamp();
            _started = true;
        }

        // The bracket's two ends go through the terminal path (bobcat#767): a run whose
        // run_started was dropped is a run no board ever shows.
        _sink.PostTerminal(new RunStarted(
            _info.RunId, _info.Suite, _info.Repository, _info.Branch, _info.Mode,
            DateTimeOffset.UtcNow, totalTests, _info.Tag, _info.Session, _info.Command));

        _heartbeat = new Timer(
            _ => _sink.Post(new RunHeartbeat(_info.RunId, DateTimeOffset.UtcNow)),
            null, heartbeatInterval, heartbeatInterval);
    }

    public void RunFinished(SupervisorResults results)
    {
        stopHeartbeat();

        // Nothing may follow the terminal record: a trailing progress post that fired after it
        // would re-open a finished run on any board folding latest-wins. The gate makes the order
        // certain — a trailing post already running completes before _finished is set, and one
        // that starts afterwards sees it and posts nothing.
        lock (_progress)
        {
            _finished = true;
            _trailing?.Dispose();
            _trailing = null;
        }

        // The terminal record is never dropped under backpressure (bobcat#767), and arrives
        // stamped with each target's own drop count when there was one.
        _sink.PostTerminal(new RunFinished(
            _info.RunId,
            results.ExitCode,
            Passed: results.CleanPasses.Count,
            // Indeterminate is NOT folded into Failed: "we don't know what happened" and
            // "it failed" are different situations — same reasoning as exit code 2 vs 1.
            Failed: results.Failed.Count(t => !t.IsIndeterminate),
            PassedOnRetry: results.PassedOnRetry.Count,
            Indeterminate: results.Indeterminate.Count,
            DateTimeOffset.UtcNow,
            // Never folded into Passed (bobcat#655): nobody ran a skipped test.
            Skipped: results.Skipped.Count));
    }

    /// <summary>
    /// One sentence naming every monitor target that dropped events under backpressure during
    /// this run, or null — for the supervisor's log, once, at the end (bobcat#767). Only a sink
    /// this publisher connected itself can answer; a sink the consumer injected keeps its own
    /// accounts.
    /// </summary>
    public string? DropReport() => (_sink as MonitorTargets)?.DropReport();

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
    /// — its tracking is per run request, and the MTP host starts a fresh run for every request,
    /// so even a same-process retry restarts at one. The supervisor holds the only true count, and a projection that folds
    /// this event knows the next <c>ScenarioStarted</c> is that attempt however the worker
    /// numbered it. Without it a supervised retry overwrote its own previous attempt and
    /// CTRF's <c>retryAttempts[]</c> worked for in-process retries only.
    /// </para>
    /// <para>
    /// For the progress counts (bobcat#767) this is the moment a test stops being failed: it
    /// will run again, so it is neither finished nor failed until it does, and it joins
    /// <c>retried</c> for good.
    /// </para>
    /// </remarks>
    public void RetryScheduled(string uid, int nextAttempt, Disposition disposition)
    {
        bool changed, failedChanged;
        lock (_progress)
        {
            var joined = _retried.Add(uid);
            (changed, failedChanged) = setVerdict(uid, ProgressVerdict.PendingRetry);
            changed |= joined;
        }

        _sink.Post(new RetryScheduled(
            _info.RunId, uid, nextAttempt, disposition.Kind.ToString(), disposition.Reason));

        if (changed) progressChanged(failedChanged);
    }

    /// <summary>
    /// The authoritative verdict for one attempt, with the policy's decision (bobcat#767). It
    /// usually restates what the live stream already said; what it adds is the cases the live
    /// stream cannot carry — a crashed worker's Indeterminate padding, and a failure the policy
    /// is about to retry, which is pending rather than failed.
    /// </summary>
    public void AttemptRecorded(string uid, SupervisorAttempt attempt)
    {
        // An unsupported retry (resolve's "this attempt was NOT retried") keeps its retry kind
        // on the disposition and says so in Unsupported — it is a failure, not a pending one.
        var verdict = attempt.Disposition.IsRetry && attempt.Unsupported is null
            ? ProgressVerdict.PendingRetry
            : verdictOf(attempt.Outcome.State);

        bool changed, failedChanged;
        lock (_progress) (changed, failedChanged) = setVerdict(uid, verdict);

        if (changed) progressChanged(failedChanged);
    }

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
    /// run whose workers do not publish for themselves ever produces: a plain xUnit worker
    /// publishes nothing, so without this a supervised suite of
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
    /// <para>
    /// <strong>What makes that fold exact is <c>SpecId</c></strong> (issue #515). The uid here is
    /// the platform's and the scenario stream's is the spec identity, so for the whole of the
    /// projected lane the two never matched and a console had to fold them by method name —
    /// ambiguous across classes and overloads. The identity now travels beside the uid, taken
    /// from the suite's own manifest through <see cref="SpecUidJoin"/> (the same join the resident
    /// runner runs a named specification with), and is null for any test no manifest names.
    /// </para>
    /// <para>
    /// This stream is also what moves the <c>run_progress</c> counts between lanes returning
    /// (bobcat#767): in-flight and workers busy from the starts, completed and the verdict split
    /// from the terminal states.
    /// </para>
    /// </remarks>
    public void TestUpdated(WorkerLaunchContext worker, WorkerTestUpdate update)
    {
        var at = DateTimeOffset.UtcNow;

        if (update.InProgress)
        {
            lock (_progress)
            {
                _inFlight[update.Uid] = new InFlightEntry(
                    update.DisplayName, workerKey(worker), at, _time.GetTimestamp());
            }

            _sink.Post(new TestStarted(
                _info.RunId, update.Uid, update.DisplayName, laneOf(worker), at, specIdOf(update.Uid)));

            progressChanged();
            return;
        }

        if (update.State is not { } state || state == WorkerTestState.Indeterminate) return;

        long? durationMs;
        bool failedChanged;
        lock (_progress)
        {
            durationMs = _inFlight.Remove(update.Uid, out var started)
                ? (long)(at - started.StartedAt).TotalMilliseconds
                : null;
            (_, failedChanged) = setVerdict(update.Uid, verdictOf(state));
        }

        _sink.Post(new TestFinished(
            _info.RunId, update.Uid, update.DisplayName, state.ToString(), durationMs, laneOf(worker), at,
            specIdOf(update.Uid)));

        progressChanged(failedChanged);
    }

    /// <summary>
    /// The opt-in heartbeat (issue #148) is one more occasion to post progress, and the only one
    /// that knows peak worker RSS — kept and carried on every later post, since "highest so far"
    /// never comes down.
    /// </summary>
    public void Heartbeat(SupervisorHeartbeat heartbeat)
    {
        lock (_progress)
        {
            if (heartbeat.PeakWorkerRssBytes is { } peak) _peakWorkerRssBytes = peak;
            if (!_started || _finished) return;

            postProgressLocked(_time.GetTimestamp());
        }
    }

    private static ProgressVerdict verdictOf(WorkerTestState state) => state switch
    {
        WorkerTestState.Passed => ProgressVerdict.Passed,
        WorkerTestState.Skipped => ProgressVerdict.Skipped,
        WorkerTestState.Indeterminate => ProgressVerdict.Indeterminate,
        _ => ProgressVerdict.Failed
    };

    /// <summary>Under the gate. Whether anything changed, and whether the failed count did.</summary>
    private (bool Changed, bool FailedChanged) setVerdict(string uid, ProgressVerdict verdict)
    {
        var had = _verdicts.TryGetValue(uid, out var previous);
        if (had && previous == verdict) return (false, false);

        _verdicts[uid] = verdict;
        var wasFailed = had && previous == ProgressVerdict.Failed;
        return (true, wasFailed != (verdict == ProgressVerdict.Failed));
    }

    /// <summary>
    /// The cadence rule (bobcat#767): post now when the failed count moved, when nothing has been
    /// posted yet, or when <see cref="ProgressInterval"/> has passed since the last post; otherwise
    /// arm one trailing post for the end of the interval, so the newest state always lands.
    /// </summary>
    private void progressChanged(bool failedChanged = false)
    {
        lock (_progress)
        {
            if (!_started || _finished) return;

            var now = _time.GetTimestamp();
            if (failedChanged
                || _lastProgressAt is not { } last
                || _time.GetElapsedTime(last, now) >= ProgressInterval)
            {
                postProgressLocked(now);
                return;
            }

            if (_trailing is not null) return;

            var due = ProgressInterval - _time.GetElapsedTime(last, now);
            _trailing = _time.CreateTimer(_ => trailingDue(), null, due, Timeout.InfiniteTimeSpan);
        }
    }

    private void trailingDue()
    {
        lock (_progress)
        {
            _trailing?.Dispose();
            _trailing = null;
            if (!_started || _finished) return;

            postProgressLocked(_time.GetTimestamp());
        }
    }

    /// <summary>Under the gate: post the current counts and reset the cadence.</summary>
    private void postProgressLocked(long now)
    {
        _trailing?.Dispose();
        _trailing = null;
        _lastProgressAt = now;

        string? longestUid = null, longestName = null;
        long? longestMs = null;
        foreach (var (uid, entry) in _inFlight)
        {
            var inFlight = (long)_time.GetElapsedTime(entry.StartedStamp, now).TotalMilliseconds;
            if (longestMs is { } best && inFlight <= best) continue;

            (longestUid, longestName, longestMs) = (uid, entry.DisplayName, inFlight);
        }

        var passed = 0;
        var failed = 0;
        var completed = 0;
        foreach (var verdict in _verdicts.Values)
        {
            switch (verdict)
            {
                case ProgressVerdict.PendingRetry:
                    continue;
                case ProgressVerdict.Passed:
                    passed++;
                    break;
                case ProgressVerdict.Failed:
                    failed++;
                    break;
            }

            completed++;
        }

        _sink.Post(new RunProgress(
            _info.RunId,
            (long)_time.GetElapsedTime(_runStartedAt, now).TotalMilliseconds,
            completed,
            _total ?? 0,
            _inFlight.Count,
            longestUid,
            longestName,
            longestMs,
            _peakWorkerRssBytes,
            DateTimeOffset.UtcNow,
            Passed: passed,
            Failed: failed,
            Retried: _retried.Count,
            WorkersBusy: _inFlight.Values.Select(e => e.Worker).Distinct().Count()));
    }

    private string? specIdOf(string uid) => _specIds.GetValueOrDefault(uid);

    private static int? laneOf(WorkerLaunchContext worker)
        => worker.Purpose == WorkerPurpose.Lane ? worker.Lane : null;

    /// <summary>
    /// One process, however it is named: a lane's worker and its replacement are different
    /// processes, and an isolated one-test process is its own.
    /// </summary>
    private static string workerKey(WorkerLaunchContext worker)
        => $"{worker.Purpose}:{worker.Lane}:{worker.ProcessId?.ToString() ?? "-"}";

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

        lock (_progress)
        {
            _finished = true;
            _trailing?.Dispose();
            _trailing = null;
        }

        if (_ownedPublisher != null) await _ownedPublisher.DisposeAsync();
    }
}
