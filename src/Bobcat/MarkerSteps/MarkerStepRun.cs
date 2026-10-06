using System.Reflection;
using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Resilience;

namespace Bobcat;

/// <summary>
/// The runner-neutral half of a marker-step adapter (issue #110): the process-wide run bracket a
/// scenario is opened inside, and the verdict handling that closes one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is in core rather than in each adapter.</b> Everything here is knowledge that only
/// lives in Bobcat — where a run's identity comes from, who owns the run bracket, and what a
/// verdict has to say to be worth publishing. The first hand-rolled adapter (Marten's, which the
/// docs invited people to paste) got all three wrong in ways a green suite could never reveal:
/// it minted its own <c>RunId</c> and dropped <c>BOBCAT_RUN_TAG</c>, so its evidence could not be
/// attributed to the plan node that asked for it; it published a <c>RunStarted</c> it might not
/// own; and it never set <see cref="ScenarioRecorder.Recording.Failure"/>, so every scenario it
/// ever reported was a <c>CleanPass</c>.
/// </para>
/// <para>
/// An adapter package is therefore only the two callbacks its runner happens to spell differently.
/// </para>
/// <para>
/// <b>Inert when nothing is listening.</b> With no console on the wire the publisher is null and
/// the whole path costs a few strings per test.
/// </para>
/// </remarks>
public static class MarkerStepRun
{
    private static readonly object _gate = new();
    private static IMonitorEventSink? _sink;
    private static MonitorRunInfo? _info;
    private static Timer? _heartbeat;
    private static bool _started;
    private static int _passed;
    private static int _failed;

    /// <summary>
    /// Whether the console probe has already run in this process. Separate from
    /// <see cref="_info"/> because the sink is a PROCESS fact and the run is a REQUEST fact
    /// (issue #402) — a warm process opens many brackets and must not re-probe 5525 for each.
    /// </summary>
    private static bool _sinkResolved;

    /// <summary>Whether the process-exit backstop is subscribed. Once per process, not per run.</summary>
    private static bool _exitHooked;

    /// <summary>How often the run posts a heartbeat while it owns the bracket.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Open a scenario for <paramref name="method"/>, starting the run bracket on first use.
    /// </summary>
    /// <param name="method">The test method. Its name and declaring type supply the identity.</param>
    /// <param name="mode">
    /// How the run was executed — "xunit", "tunit". Recorded on <c>RunStarted</c>; the first call
    /// in a process fixes it, because a run has one mode.
    /// </param>
    public static ScenarioRecorder.Recording BeginScenario(MethodInfo method, string mode)
        => BeginScenario(method.DeclaringType, method.Name, mode);

    /// <summary>
    /// Open a scenario for a test the runner identifies by type and method name rather than by
    /// reflection.
    /// </summary>
    /// <remarks>
    /// Not a convenience overload — TUnit is why it exists. Its <c>TestDetails</c> carries
    /// <c>ClassType</c> and <c>MethodName</c> and hands out no <see cref="MethodInfo"/> at all,
    /// which is the price of being AOT-friendly. Any adapter can reach this shape; not every
    /// adapter can reach the other one.
    /// </remarks>
    public static ScenarioRecorder.Recording BeginScenario(Type? declaringType, string methodName, string mode)
    {
        var info = OpenRun(mode);

        return ScenarioRecorder.Begin(
            FeatureNameFor(declaringType), ScenarioNameFor(methodName), _sink, info.RunId);
    }

    /// <summary>
    /// Close the scenario in progress with the verdict its runner reported.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="ScenarioRecorder.Current"/> rather than taking a handle: the recording is
    /// already ambient (it has to be, for a generated interceptor to find it), so an adapter that
    /// held its own reference would be keeping per-test state on an attribute instance the runner
    /// is free to share.
    /// </remarks>
    /// <returns>
    /// The exception the adapter must throw to make the runner agree with the specification, or
    /// null when the two already agree.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Why a verdict can come back OUT of here.</b> <see cref="SpecAssert"/> records a wrong
    /// without throwing, so that a spec shows every disagreement instead of only its first — which
    /// means a scenario can finish red while the test method finished without an exception and the
    /// runner is about to call it green. The gathered failures are the specification's verdict, and
    /// the only way to hand them to the runner is to throw where the runner is still listening: an
    /// adapter's after-test hook.
    /// </para>
    /// <para>
    /// The runner's verdict still wins whenever it HAS one — a thrown assertion, an error, a skip —
    /// because that is the contract this whole lane rests on. This only speaks up for the case the
    /// runner cannot see.
    /// </para>
    /// </remarks>
    public static Exception? EndScenario(ScenarioVerdict verdict)
    {
        var recording = ScenarioRecorder.Current;
        if (recording is null) return null;

        switch (verdict.Kind)
        {
            case ScenarioVerdictKind.NotClaimed:
                // A skipped test asserted nothing. Publishing CleanPass for it would be the same
                // lie as publishing CleanPass for a failure, so the scenario is withdrawn instead.
                recording.Cancel();
                return null;

            case ScenarioVerdictKind.Failed:
                recording.FailureDescription = verdict.Describe();
                Interlocked.Increment(ref _failed);
                recording.Dispose();
                return null;

            default:
                var gathered = recording.GatheredFailures();
                if (gathered is null) Interlocked.Increment(ref _passed);
                else Interlocked.Increment(ref _failed);

                recording.Dispose();
                return gathered is null ? null : new SpecAssertionException(gathered);
        }
    }

    /// <summary>The feature title: <c>[BobcatFeature]</c>'s title, else the class name derived.</summary>
    public static string FeatureNameFor(MethodInfo method) => FeatureNameFor(method.DeclaringType);

    /// <summary>The feature title for a test class.</summary>
    public static string FeatureNameFor(Type? declaringType) => MarkerSpecNaming.FeatureTitle(declaringType);

    /// <summary>The scenario title: the method name derived.</summary>
    public static string ScenarioNameFor(MethodInfo method) => MarkerSpecNaming.ScenarioTitle(method);

    /// <summary>The scenario title for a method name, for a runner that hands out no MethodInfo.</summary>
    public static string ScenarioNameFor(string methodName) => MarkerSpecNaming.ScenarioTitle(methodName);

    /// <summary>
    /// A C# identifier as the sentence it was standing in for. Kept as the spelling callers know;
    /// <see cref="MarkerSpecNaming"/> owns the rule, and the generator holds the copy that has to
    /// agree with it.
    /// </summary>
    public static string Prettify(string name) => MarkerSpecNaming.Prettify(name);

    /// <summary>
    /// Open a run bracket, for a caller that knows when a run <em>request</em> begins (issue #402).
    /// Idempotent: a bracket already open is returned as it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is public now.</b> A projected suite used to open its run on the first scenario
    /// and close it from <c>ProcessExit</c>. For a one-shot <c>dotnet test</c> that is exactly
    /// right — one process, one run. In a live server-mode process it is wrong three ways at once:
    /// two run requests published ONE <c>run_started</c>, shared one <c>RunId</c>, and produced NO
    /// <c>run_finished</c>, so the second command's scenarios landed on the first command's card
    /// and the run never closed. A run with no finish is the shape of a wedged one, which is
    /// exactly what issue #195 was opened for.
    /// </para>
    /// <para>
    /// The caller that knows is <c>Bobcat.Xunit</c>'s <c>ITestSessionLifetimeHandler</c>. Measured
    /// on Microsoft.Testing.Platform 1.9.1: that hook fires <b>once per <c>testing/runTests</c>
    /// request</b>, each with its own <c>SessionUid</c> — three requests in one process gave three
    /// distinct, properly bracketed sessions — and <b>discovery opens no session at all</b>, which
    /// is what keeps a listing from putting an empty card on the board.
    /// </para>
    /// <para>
    /// <b>The run info is re-discovered per bracket, and that is the point.</b>
    /// <c>BOBCAT_RUN_COMMAND</c> (issue #392) is read here rather than once per process, so a
    /// process that serves several commands reads the current one each time instead of stamping
    /// every run with the first. The sink is NOT re-resolved: that is a process fact, and probing
    /// 5525 per request would charge every command for a console handshake.
    /// </para>
    /// </remarks>
    /// <param name="mode">How the run was executed — "xunit", "tunit".</param>
    public static MonitorRunInfo OpenRun(string mode)
    {
        lock (_gate)
        {
            ensureSink();

            if (_info is not null) return _info;

            var info = MonitorRunInfo.Discover(mode);
            _info = info;

            if (_sink is not null && !info.HasExternalOwner)
            {
                // TotalScenarios is null on purpose. The runner owns discovery here and has not
                // told us how many tests it intends to run — and a wrong total is worse than none.
                _sink.Post(new RunStarted(
                    info.RunId, info.Suite, info.Repository, info.Branch, info.Mode,
                    DateTimeOffset.UtcNow, TotalScenarios: null, Tag: info.Tag, Session: info.Session,
                    Command: info.Command));

                _heartbeat = new Timer(
                    _ => _sink?.Post(new RunHeartbeat(info.RunId, DateTimeOffset.UtcNow)),
                    null, HeartbeatInterval, HeartbeatInterval);

                _started = true;
            }

            return info;
        }
    }

    /// <summary>
    /// Close the run bracket this process has open, if any (issue #402). Idempotent, and safe to
    /// call from a caller that does not know whether anyone already closed it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sink survives.</b> Closing a bracket forgets the run — so the next
    /// <see cref="OpenRun"/> mints a fresh <c>RunId</c> with fresh counts — but keeps the publisher,
    /// because in a warm process the next request needs it. Disposing it here was the one-shot
    /// code's behaviour and would have stopped the pump after the first command.
    /// </para>
    /// <para>
    /// Ordering needs no flush: the publisher drains one bounded channel FIFO, so a
    /// <c>run_finished</c> posted before the next <c>run_started</c> reaches the console in that
    /// order.
    /// </para>
    /// </remarks>
    public static void CloseRun()
    {
        lock (_gate)
        {
            if (_info is null) return;

            if (_started)
            {
                _sink?.Post(new RunFinished(
                    _info.RunId,
                    ExitCode: _failed > 0 ? 1 : 0,
                    Passed: _passed,
                    Failed: _failed,
                    PassedOnRetry: 0,
                    Indeterminate: 0,
                    FinishedAt: DateTimeOffset.UtcNow));
            }

            stopHeartbeat();

            _started = false;
            _info = null;
            _passed = 0;
            _failed = 0;
        }
    }

    /// <summary>
    /// Resolve the console once per process: the local spec report, then the probe.
    /// </summary>
    /// <remarks>
    /// The local spec report is the one output that has to survive a run with no console listening,
    /// and this is the first moment a projected run announces itself — so it comes before
    /// everything, including the probe whose answer settles its default.
    /// </remarks>
    private static void ensureSink()
    {
        if (_sinkResolved) return;
        _sinkResolved = true;

        ProjectedSpecConsole.EnableIfRequested();

        // Never let a missing or slow console matter to a test run: TryConnect probes once
        // with a tight timeout and hands back null when nothing answers.
        _sink = MonitorPublisher.TryConnect().GetAwaiter().GetResult();

        // Now that we know whether anything answered, the local report can take its default:
        // on in a terminal with nothing listening, which is exactly the run that would otherwise
        // produce no specification anywhere (issue #384). An explicit BOBCAT_SPEC_CONSOLE, in
        // either direction, was already honoured above and is not revisited.
        ProjectedSpecConsole.EnableByDefault(wireIsLive: _sink is not null);

        if (_exitHooked) return;
        _exitHooked = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => closeAtProcessExit();
    }

    /// <summary>
    /// The backstop (issue #402). It <b>has to stay</b>: it is the whole bracket for a caller with
    /// no session hook — a TUnit suite, or any host whose platform extension is not registered —
    /// where the run really is the process, and nothing here knows which scenario is the last one.
    /// </summary>
    /// <remarks>
    /// <b>It drains unconditionally, which the per-bracket close deliberately does not.</b> When a
    /// session hook already closed the bracket there is nothing left to post, but the
    /// <c>run_finished</c> it posted may still be sitting in the channel — and the old code's
    /// early return (it only drained when it had a bracket to close) would have let the last run of
    /// every warm process die there.
    /// </remarks>
    private static void closeAtProcessExit()
    {
        CloseRun();

        lock (_gate)
        {
            // Drain before the process goes away, or RunFinished dies in the channel with it.
            if (_sink is IAsyncDisposable disposable)
            {
                disposable.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>
    /// Plain <c>Timer.Dispose()</c> does not wait for an in-flight callback, so a heartbeat could
    /// still post after <c>RunFinished</c>. The wait-handle overload is what makes "no heartbeats
    /// after the run closed" true rather than merely likely — the same rule
    /// <see cref="MonitorPublishingObserver"/> follows.
    /// </summary>
    private static void stopHeartbeat()
    {
        if (_heartbeat is null) return;

        using var stopped = new ManualResetEvent(false);
        if (_heartbeat.Dispose(stopped)) stopped.WaitOne(TimeSpan.FromSeconds(1));
        _heartbeat = null;
    }

    /// <summary>Test seam: forget the run so the next scenario starts a fresh bracket.</summary>
    internal static void Reset(IMonitorEventSink? sink = null, MonitorRunInfo? info = null)
    {
        lock (_gate)
        {
            stopHeartbeat();
            _sink = sink;
            _info = info;
            _started = false;
            _passed = 0;
            _failed = 0;

            // The sink is whatever the caller just installed, so the probe must not run and
            // overwrite it. _exitHooked is deliberately NOT reset: the handler is idempotent and
            // unsubscribing a lambda nobody kept a reference to is not possible anyway.
            _sinkResolved = true;
        }
    }

    /// <summary>Test seam: the counts <c>RunFinished</c> would report right now.</summary>
    internal static (int Passed, int Failed) Counts => (_passed, _failed);

    /// <summary>Test seam: publish the run bracket's close without waiting for process exit.</summary>
    internal static void FinishForTesting() => CloseRun();

    /// <summary>Test seam: start the bracket with whatever sink <see cref="Reset"/> installed.</summary>
    internal static void StartForTesting(MonitorRunInfo info)
    {
        lock (_gate)
        {
            _info = info;

            if (_sink is not null && !info.HasExternalOwner)
            {
                _sink.Post(new RunStarted(
                    info.RunId, info.Suite, info.Repository, info.Branch, info.Mode,
                    DateTimeOffset.UtcNow, TotalScenarios: null, Tag: info.Tag, Session: info.Session,
                    Command: info.Command));
                _started = true;
            }
        }
    }
}

/// <summary>What a runner said about a test, in the only three shapes Bobcat can act on.</summary>
public enum ScenarioVerdictKind
{
    /// <summary>The test ran and passed.</summary>
    Passed,

    /// <summary>The test ran and failed.</summary>
    Failed,

    /// <summary>
    /// Skipped, or never run. The scenario made no claim, so it is withdrawn rather than reported
    /// — <see cref="RunOutcome"/> has no vocabulary for "did not happen", and the nearest word it
    /// does have (<c>CleanPass</c>) would be false.
    /// </summary>
    NotClaimed
}

/// <summary>
/// A runner's verdict, translated out of that runner's vocabulary and into Bobcat's.
/// </summary>
/// <remarks>
/// The exception details are carried as strings because that is what the runners hand over:
/// xUnit v3 reports <c>ExceptionTypes</c> and <c>ExceptionMessages</c> on
/// <c>TestContext.Current.TestState</c>, not the exception itself.
/// </remarks>
public readonly record struct ScenarioVerdict(
    ScenarioVerdictKind Kind,
    string? FailureType = null,
    string? FailureMessage = null)
{
    public static ScenarioVerdict Passed { get; } = new(ScenarioVerdictKind.Passed);

    public static ScenarioVerdict NotClaimed { get; } = new(ScenarioVerdictKind.NotClaimed);

    public static ScenarioVerdict Failed(string? failureType, string? failureMessage)
        => new(ScenarioVerdictKind.Failed, failureType, failureMessage);

    public static ScenarioVerdict FromException(Exception? exception)
        => exception is null
            ? Passed
            : new ScenarioVerdict(ScenarioVerdictKind.Failed, exception.GetType().FullName, exception.Message);

    /// <summary>The one-line failure a viewer shows. Never null for a failed verdict.</summary>
    public string Describe()
        => (FailureType, FailureMessage) switch
        {
            (null or "", null or "") => "Failed",
            (null or "", var message) => message!,
            (var type, null or "") => type!,
            var (type, message) => $"{type}: {message}"
        };
}
