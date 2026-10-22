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
    private static int _acceptanceFailing;

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
    /// <param name="method">The test method. Its name and the class it was reflected from supply the identity.</param>
    /// <param name="mode">
    /// How the run was executed — "xunit", "tunit". Recorded on <c>RunStarted</c>; the first call
    /// in a process fixes it, because a run has one mode.
    /// </param>
    public static ScenarioRecorder.Recording BeginScenario(MethodInfo method, string mode)
        => BeginScenario(method, mode, null, null);

    /// <summary>
    /// Open one row of a data-driven scenario for <paramref name="method"/> (bobcat#569): its
    /// identity is the method's plus the row — <c>(amount: 100, tier: Gold)</c>, or <c>[label]</c>
    /// when the row has a label. With no arguments and no label this is the plain scenario.
    /// </summary>
    /// <param name="method">The test method. Its name, declaring type and parameter names supply the identity.</param>
    /// <param name="mode">How the run was executed — "xunit", "tunit".</param>
    /// <param name="arguments">The values the runner passes this row, in parameter order.</param>
    /// <param name="label">The row's label, if the runner carries one.</param>
    public static ScenarioRecorder.Recording BeginScenario(
        MethodInfo method, string mode, IReadOnlyList<object?>? arguments, string? label)
    {
        // The TEST CLASS, not the method's declaring type (bobcat#600): a test inherited from a shared
        // base is reflected off the concrete subclass the runner is running, and that subclass is the
        // feature. Keyed on the declaring type, every subclass collapsed into the base's identity.
        var testClass = method.ReflectedType ?? method.DeclaringType;
        var info = openRun(mode, testClass?.Assembly);

        var row = SpecRows.Describe(parameterNamesOf(method), arguments, label);

        // The method itself, not its name: the explicit name (bobcat#489) is on THIS method, and a
        // lookup by name could land on an overload.
        var scenario = ScenarioNameFor(method);

        // A data-driven row takes its method's lifecycle (bobcat#545): it is the method that declares it.
        return begin(FeatureNameFor(testClass), scenario, row, inheritedFrom(testClass, method.DeclaringType, scenario),
            info, SpecLifecycle.Of(method));
    }

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
        => BeginScenario(declaringType, methodName, mode, null, null);

    /// <summary>
    /// Open one row of a data-driven scenario for a test the runner identifies by type and method
    /// name (bobcat#569). The parameter names come from the method of that name whose parameter count
    /// matches the row; a row with no arguments and no label is the plain scenario.
    /// </summary>
    public static ScenarioRecorder.Recording BeginScenario(
        Type? declaringType, string methodName, string mode, IReadOnlyList<object?>? arguments, string? label)
    {
        var info = openRun(mode, declaringType?.Assembly);

        var row = SpecRows.Describe(parameterNamesOf(declaringType, methodName, arguments?.Count ?? 0), arguments, label);

        // declaringType is the test class the runner names, which for an inherited test is the
        // subclass (bobcat#600); the method of that name says which class wrote the body.
        var scenario = ScenarioNameFor(declaringType, methodName);
        var bodyType = methodOf(declaringType, methodName, arguments?.Count ?? 0)?.DeclaringType;
        return begin(FeatureNameFor(declaringType), scenario, row, inheritedFrom(declaringType, bodyType, scenario), info,
            SpecLifecycle.Of(declaringType, methodName));
    }

    /// <summary>
    /// The identity the test body has under the base class that declares it, when that is not the
    /// test class (bobcat#600) — where its declared and planned steps were registered — else null.
    /// </summary>
    private static string? inheritedFrom(Type? testClass, Type? bodyType, string scenario)
    {
        if (testClass is null || bodyType is null || testClass == bodyType) return null;

        return Runtime.SpecIdentity.Of(FeatureNameFor(bodyType), scenario);
    }

    private static IReadOnlyList<string> parameterNamesOf(MethodBase method)
        => method.GetParameters().Select(x => x.Name ?? "arg" + x.Position).ToList();

    private static IReadOnlyList<string> parameterNamesOf(Type? declaringType, string methodName, int count)
    {
        if (count == 0) return [];

        var method = methodOf(declaringType, methodName, count);
        return method is null ? [] : parameterNamesOf(method);
    }

    /// <summary>
    /// The method of that name on the test class, inherited ones included: the one whose parameter
    /// count matches the row, else the first.
    /// </summary>
    private static MethodInfo? methodOf(Type? testClass, string methodName, int count)
    {
        if (testClass is null) return null;

        const BindingFlags everything =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        var candidates = testClass.GetMethods(everything).Where(m => m.Name == methodName).ToList();
        return candidates.FirstOrDefault(m => m.GetParameters().Length == count) ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Open the scenario, unless the environment is already lost (bobcat#539): then it is published
    /// as failed with the reason, closed, and the adapter's opening hook throws, which every runner
    /// reports as the test failing without its body having run.
    /// </summary>
    /// <remarks>
    /// Closed here rather than left for the adapter's after-test hook, because not every runner calls
    /// that hook once its before-test hook has thrown. A closed recording is no longer
    /// <see cref="ScenarioRecorder.Current"/>, so a hook that does run finds nothing to close twice.
    /// </remarks>
    private static ScenarioRecorder.Recording begin(
        string feature, string scenario, string? row, string? inheritedFrom, MonitorRunInfo info, Lifecycle lifecycle)
    {
        SpecEnvironment.Arm();

        var recording = ScenarioRecorder.Begin(feature, scenario, row, inheritedFrom, _sink, info.RunId);
        recording.Lifecycle = lifecycle;
        if (SpecEnvironment.Lost is not { } loss) return recording;

        var halted = new SpecEnvironmentLostException(loss);
        recording.Failure = halted;

        // Thrown from the before-test hook, so the runner fails the test and no adapter can report it
        // skipped: an Acceptance spec halted here is a plain failure on every Bobcat surface too.
        recording.CannotBeSkipped = true;
        Interlocked.Increment(ref _failed);
        recording.Dispose();

        throw halted;
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
    public static Exception? EndScenario(ScenarioVerdict verdict) => CloseScenario(verdict).Rethrow;

    /// <summary>
    /// Close the scenario in progress with the verdict its runner reported, and say what the adapter
    /// must do to make the runner agree with the specification — rethrow a gathered failure, or
    /// report a failing Acceptance spec (bobcat#545) as skipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An Acceptance failure is recorded in full and only then softened.</b> The scenario closes
    /// as <c>acceptance: failing</c> with every step and cell — console, JSON, the wire — and the
    /// adapter turns the runner's verdict into a skip whose reason starts
    /// <see cref="SpecLifecycle.SkipReasonPrefix"/>. Nothing is swallowed, which is what separates
    /// this from the run-and-absorb #404 rejected. Under <see cref="SpecLifecycle.Strict"/> it is an
    /// ordinary failure.
    /// </para>
    /// <para>
    /// The adapter does the skipping because only it can: each runner spells "skip a test that
    /// already failed" differently, and in xUnit it is not a throw at all.
    /// </para>
    /// </remarks>
    /// <param name="verdict">What the runner said.</param>
    /// <param name="canSkipAFailedTest">
    /// False for an adapter whose runner cannot turn a test that already failed into a skip (MSTest's
    /// global hooks: the more important outcome wins, and Failed outranks Inconclusive). A failing
    /// Acceptance spec the runner already failed is then recorded as the failure it is, not as
    /// <c>acceptance: failing</c>, so Bobcat never claims the build stayed green when it did not.
    /// </param>
    public static ScenarioClose CloseScenario(ScenarioVerdict verdict, bool canSkipAFailedTest = true)
    {
        var recording = ScenarioRecorder.Current;
        if (recording is null) return ScenarioClose.Nothing;

        if (!canSkipAFailedTest && verdict.Kind == ScenarioVerdictKind.Failed) recording.CannotBeSkipped = true;

        // A lost environment (bobcat#539) is never softened by Acceptance: the scenario that threw the
        // SpecCatastrophicException is the cause every halted scenario after it names, so it stays a
        // failure rather than reading as a skip.
        if (SpecEnvironment.Lost is { Spec: { } lostIn } && lostIn == recording.Uid) recording.CannotBeSkipped = true;

        switch (verdict.Kind)
        {
            case ScenarioVerdictKind.NotClaimed:
                // A skipped test asserted nothing. Publishing CleanPass for it would be the same
                // lie as publishing CleanPass for a failure, so the scenario is withdrawn instead.
                recording.Cancel();
                return ScenarioClose.Nothing;

            case ScenarioVerdictKind.Failed:
                recording.FailureDescription = verdict.Describe();
                recording.Dispose();

                if (recording.AcceptanceFailing)
                {
                    Interlocked.Increment(ref _acceptanceFailing);
                    return ScenarioClose.SkipAsAcceptance(SpecLifecycle.FirstFailure(recording.FailureDescription));
                }

                Interlocked.Increment(ref _failed);
                return ScenarioClose.Nothing;

            default:
                var gathered = recording.GatheredFailures();
                recording.Dispose();

                if (gathered is null)
                {
                    Interlocked.Increment(ref _passed);
                    return ScenarioClose.Nothing;
                }

                if (recording.AcceptanceFailing)
                {
                    Interlocked.Increment(ref _acceptanceFailing);
                    return ScenarioClose.SkipAsAcceptance(SpecLifecycle.FirstFailure(gathered));
                }

                Interlocked.Increment(ref _failed);
                return new ScenarioClose(new SpecAssertionException(gathered), null);
        }
    }

    /// <summary>
    /// Fail a <c>Pending = true</c> specification in a strict run (C13): open its scenario, close it
    /// failed with <c>pending specification in strict mode: &lt;identity&gt;</c> so every Bobcat surface
    /// shows it, and hand back the exception the adapter throws so the runner fails it too.
    /// </summary>
    /// <remarks>
    /// Called from the adapter's before-test hook, which throws the result: the body never runs, because
    /// a stub has nothing to say, and the after-test hook never runs either, which is why the scenario
    /// is opened and closed here in one go.
    /// </remarks>
    public static PendingSpecificationException FailPendingInStrictMode(MethodInfo method, string mode)
    {
        var recording = BeginScenario(method, mode);
        var failure = new PendingSpecificationException(recording.Uid);
        recording.FailureDescription = failure.Message;
        recording.Dispose();
        Interlocked.Increment(ref _failed);
        return failure;
    }

    /// <summary>
    /// The feature title: <c>[BobcatFeature]</c>'s title, else the class name derived — of the test
    /// class the method was reflected from, which for an inherited test is the subclass (bobcat#600).
    /// </summary>
    public static string FeatureNameFor(MethodInfo method) => FeatureNameFor(method.ReflectedType ?? method.DeclaringType);

    /// <summary>The feature title for a test class.</summary>
    public static string FeatureNameFor(Type? testClass) => MarkerSpecNaming.FeatureTitle(testClass);

    /// <summary>The scenario title: the explicit name the method states (bobcat#489), else the method name derived.</summary>
    public static string ScenarioNameFor(MethodInfo method) => MarkerSpecNaming.ScenarioTitle(method);

    /// <summary>The scenario title for a bare method name: the method name derived, no explicit name consulted.</summary>
    public static string ScenarioNameFor(string methodName) => MarkerSpecNaming.ScenarioTitle(methodName);

    /// <summary>
    /// The scenario title for a test a runner identifies by type and method name: its explicit name
    /// (bobcat#489) when the method states one, else the method name derived.
    /// </summary>
    public static string ScenarioNameFor(Type? declaringType, string methodName)
        => MarkerSpecNaming.ScenarioTitle(declaringType, methodName);

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
    public static MonitorRunInfo OpenRun(string mode) => openRun(mode, null);

    /// <summary>
    /// <see cref="OpenRun"/>, plus the assembly of the scenario that opened the bracket when there
    /// is one, which is the second place <see cref="publishEventModel"/> looks for a spec half.
    /// </summary>
    private static MonitorRunInfo openRun(string mode, Assembly? scenarioAssembly)
    {
        lock (_gate)
        {
            ensureSink();

            if (_info is not null) return _info;

            var info = MonitorRunInfo.Discover(mode);
            _info = info;

            // The results file (issue #540) is per bracket too, so a warm process writes one per request.
            Rendering.ResultsFile.BeginRun(info);

            if (_sink is not null && !info.HasExternalOwner)
            {
                // TotalScenarios is null on purpose. The runner owns discovery here and has not
                // told us how many tests it intends to run — and a wrong total is worse than none.
                _sink.PostTerminal(new RunStarted(
                    info.RunId, info.Suite, info.Repository, info.Branch, info.Mode,
                    DateTimeOffset.UtcNow, TotalScenarios: null, Tag: info.Tag, Session: info.Session,
                    Command: info.Command));

                _heartbeat = new Timer(
                    _ => _sink?.Post(new RunHeartbeat(info.RunId, DateTimeOffset.UtcNow)),
                    null, HeartbeatInterval, HeartbeatInterval);

                _started = true;
            }

            publishEventModel(scenarioAssembly);

            return info;
        }
    }

    /// <summary>
    /// Test seam: where a new bracket finds the Event Model halves it publishes. The default is
    /// <see cref="specHalves"/>; <see cref="Reset"/> puts it back.
    /// </summary>
    internal static Func<Assembly?, IReadOnlyList<SpecEventModelPublisher.Half>> FindEventModelHalves
    {
        get;
        set;
    } = specHalves;

    /// <summary>
    /// Test seam: where the one actionable Event Model notice goes. The default is standard error;
    /// <see cref="Reset"/> puts it back.
    /// </summary>
    internal static Action<string> EventModelNotice { get; set; } = writeNotice;

    /// <summary>
    /// Publish the spec assembly's half of the Event Model when this bracket has a console to send
    /// it to (bobcat#488).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why here.</b> The half goes out once per run, which for a resident runner means once per
    /// command, cold or warm. A projected run is this bracket, so this is the moment: a one-shot <c>dotnet test</c>
    /// sends it once, and a warm process sends it once per run request, because the session hook
    /// opens one bracket per request. It is a <c>PUT</c> under a stable source name, so a resend
    /// replaces the last one rather than piling up, and it puts the half back on a console that was
    /// restarted between two commands.
    /// </para>
    /// <para>
    /// <b>It lives in core rather than in <c>Bobcat.Xunit</c>'s session hook</b> so that every
    /// adapter gets it. A TUnit, NUnit or MSTest suite has no session hook and opens its bracket on
    /// the first scenario, so it publishes once per process, which for those runners is the run.
    /// </para>
    /// <para>
    /// The same bounds as the event pump: nothing runs without a live <see cref="MonitorTargets"/>
    /// (so <c>BOBCAT_MONITOR=0</c> or no console means no request at all), the exchange is held to
    /// <see cref="SpecEventModelPublisher.Ceiling"/>, and nothing here can fail a run. It goes to
    /// <b>every</b> target the run is attached to (bobcat#530), each asked its own model-name
    /// question.
    /// </para>
    /// </remarks>
    private static void publishEventModel(Assembly? scenarioAssembly)
    {
        if (_sink is not MonitorTargets targets) return;

        try
        {
            var halves = FindEventModelHalves(scenarioAssembly);
            if (halves.Count == 0) return;

            SpecEventModelPublisher.PublishAll(targets, halves, EventModelNotice).GetAwaiter().GetResult();
        }
        catch
        {
            // A spec half that will not publish is never a reason to disturb a run.
        }
    }

    /// <summary>
    /// The halves to publish: the entry assembly's, which is the spec assembly in every
    /// Microsoft.Testing.Platform host because the test project IS the executable, and the opening
    /// scenario's, for a host where it is not (VSTest's <c>testhost</c>). Discovery skips duplicates
    /// and any assembly with no generated <c>BobcatEventModelSource</c>.
    /// </summary>
    private static IReadOnlyList<SpecEventModelPublisher.Half> specHalves(Assembly? scenarioAssembly)
        => SpecEventModelPublisher.Discover(
            new[] { Assembly.GetEntryAssembly(), scenarioAssembly }.OfType<Assembly>());

    /// <summary>
    /// Standard error, because a projected run is always inside a test platform that owns standard
    /// output, and this notice is the one thing about the Event Model a person has to act on.
    /// </summary>
    private static void writeNotice(string notice)
    {
        try
        {
            Console.Error.WriteLine(notice);
        }
        catch
        {
            // Nowhere to say it is not a reason to fail a run either.
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
                _sink?.PostTerminal(new RunFinished(
                    _info.RunId,
                    ExitCode: _failed > 0 ? 1 : 0,
                    Passed: _passed,
                    Failed: _failed,
                    PassedOnRetry: 0,
                    Indeterminate: 0,
                    FinishedAt: DateTimeOffset.UtcNow,
                    AcceptanceFailing: _acceptanceFailing));
            }

            stopHeartbeat();

            // Every scenario of this bracket has closed, so the file is complete. Never throws.
            Rendering.ResultsFile.EndRun();

            _started = false;
            _info = null;
            _passed = 0;
            _failed = 0;
            _acceptanceFailing = 0;
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

        // Never let a missing or slow console matter to a test run: TryConnect probes each target
        // once, concurrently, with a tight timeout, and hands back null when nothing answers.
        _sink = MonitorTargets.TryConnect().GetAwaiter().GetResult();

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
    /// after the run closed" true rather than merely likely.
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
            _acceptanceFailing = 0;
            FindEventModelHalves = specHalves;
            EventModelNotice = writeNotice;
            Rendering.ResultsFile.Reset();

            // The sink is whatever the caller just installed, so the probe must not run and
            // overwrite it. _exitHooked is deliberately NOT reset: the handler is idempotent and
            // unsubscribing a lambda nobody kept a reference to is not possible anyway.
            _sinkResolved = true;
        }
    }

    /// <summary>Test seam: the counts <c>RunFinished</c> would report right now.</summary>
    internal static (int Passed, int Failed) Counts => (_passed, _failed);

    /// <summary>Test seam: failing Acceptance specs so far in this bracket (bobcat#545).</summary>
    internal static int AcceptanceFailingCount => _acceptanceFailing;

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
                _sink.PostTerminal(new RunStarted(
                    info.RunId, info.Suite, info.Repository, info.Branch, info.Mode,
                    DateTimeOffset.UtcNow, TotalScenarios: null, Tag: info.Tag, Session: info.Session,
                    Command: info.Command));
                _started = true;
            }
        }
    }
}

/// <summary>
/// What an adapter must do after <see cref="MarkerStepRun.CloseScenario"/>: throw
/// <see cref="Rethrow"/> (a gathered failure the runner cannot see), report the test skipped with
/// <see cref="SkipReason"/> (a failing Acceptance spec, bobcat#545), or nothing.
/// </summary>
public readonly record struct ScenarioClose(Exception? Rethrow, string? SkipReason)
{
    /// <summary>The runner already agrees with the specification.</summary>
    public static ScenarioClose Nothing { get; } = new(null, null);

    /// <summary>
    /// A failing Acceptance spec: the adapter reports the test skipped, with the reason
    /// <c>acceptance spec, not yet passing: &lt;first failure&gt;</c>.
    /// </summary>
    public static ScenarioClose SkipAsAcceptance(string firstFailure)
        => new(null, SpecLifecycle.SkipReasonFor(firstFailure));

    /// <summary>Whether the adapter must report the test skipped.</summary>
    public bool Skip => SkipReason is not null;
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
