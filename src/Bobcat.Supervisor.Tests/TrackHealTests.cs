using Bobcat.Monitoring;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#824 — a gate heals a track's service when the unit running on it degrades. Same
/// fake-clock discipline as <see cref="StallActionTests"/>: a "service" is a flag the fake
/// workers read, a slow test is the clock advanced by the service's pace while the test is in
/// flight, and the heal hook is what flips the flag. No container anywhere.
/// </summary>
public class TrackHealTests : IDisposable
{
    private static readonly TimeSpan waitBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan known = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan degradedPace = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bobcat-track-heal-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly FakeTimeProvider _time = new();

    public TrackHealTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A Docker service as the fake workers see it: slow until healed.</summary>
    private sealed class Service
    {
        public volatile bool Degraded = true;
        public int Heals;
        public int Preflights;
        public List<TrackHealContext> HealContexts { get; } = [];
    }

    private sealed class RecordingObserver : ISupervisorObserver
    {
        public List<RunDegradation> Degradations { get; } = [];
        public List<TrackHeal> Heals { get; } = [];

        public void Degraded(RunDegradation degradation)
        {
            lock (Degradations) Degradations.Add(degradation);
        }

        public void TrackHealed(TrackHeal heal)
        {
            lock (Heals) Heals.Add(heal);
        }
    }

    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = [];

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }

    /// <summary>Tests in one class — one lane — each known to take <see cref="known"/>.</summary>
    private static IReadOnlyList<WorkerTest> suite(string className, int count)
        => Enumerable.Range(1, count).Select(i => FakeWorkerFactory.InClass(className, $"t{i}")).ToList();

    private static Dictionary<string, TimeSpan> knownDurations(params IEnumerable<WorkerTest>[] suites)
        => suites.SelectMany(s => s).ToDictionary(t => t.DisplayName, _ => known, StringComparer.Ordinal);

    /// <summary>
    /// Workers whose tests run at the service's pace: the clock moves by <see cref="degradedPace"/>
    /// while the service is degraded, by the known duration once it is healed. Only lane workers
    /// read the service, so a solo retry is never slowed by it.
    /// </summary>
    private FakeWorkerFactory pacedBy(Service service, IReadOnlyList<WorkerTest> tests, Func<string, FakeWorker, Task>? alsoHold = null)
        => new()
        {
            Tests = tests,
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (uid, worker) =>
            {
                _time.Advance(service.Degraded && worker.Launch.Purpose == WorkerPurpose.Lane ? degradedPace : known);
                return alsoHold?.Invoke(uid, worker) ?? Task.CompletedTask;
            }
        };

    /// <summary>A project the gate can resolve without building: a csproj and the host file where the SDK would put it.</summary>
    private string project(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".csproj");
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var host = Path.Combine(directory, "bin", "Debug", "net10.0", OperatingSystem.IsWindows() ? name + ".exe" : name);
        Directory.CreateDirectory(Path.GetDirectoryName(host)!);
        File.WriteAllText(host, "");
        return path;
    }

    private GateDeclaration gate() => new()
    {
        SkipBuild = true,
        PublishToMonitor = false,
        DurationsPath = null,
        WorkerBudget = 2,
        Log = line => { lock (_log) _log.Add(line); }
    };

    private Track healable(Service service, string name = "sqlserver", int? maxHeals = null, Action? afterHeal = null)
    {
        var track = new Track(name)
            .Check($"{name} answers", () => Interlocked.Increment(ref service.Preflights))
            .Heal((context, _) =>
            {
                // Recreate the containers, wait until healthy, re-create the lane catalogs —
                // modelled as: the service is fast again.
                lock (service.HealContexts) service.HealContexts.Add(context);
                Interlocked.Increment(ref service.Heals);
                service.Degraded = false;
                afterHeal?.Invoke();
                return Task.CompletedTask;
            });

        if (maxHeals is { } max) track.MaxHeals = max;
        return track;
    }

    private Action<GateUnit, Supervisor> supervise(IReadOnlyDictionary<string, TimeSpan>? durations, RecordingObserver? observer = null)
        => (_, supervisor) =>
        {
            supervisor.Time = _time;
            supervisor.KnownTestDurations = durations;
            supervisor.DegradationWindow = 3;
            if (observer is not null) supervisor.AddObserver(observer);
        };

    [Fact]
    public async Task a_unit_whose_tests_balloon_past_their_known_durations_is_healed_once_and_resumes_at_pace()
    {
        // The Wolverine shape: ~2s tests taking ~2 minutes, every one finishing well inside any
        // stall threshold. Nothing is stuck; the run is slow.
        var service = new Service();
        var tests = suite("Slow", 8);
        var factory = pacedBy(service, tests);
        var observer = new RecordingObserver();

        var declaration = gate();
        declaration.Tracks.Add(healable(service));
        declaration.Project(project("SlowSuite")).On("sqlserver").Supervise(supervise(knownDurations(tests), observer));
        declaration.WorkerFactory = _ => factory;

        var results = await declaration.Run().WaitAsync(waitBudget);

        var text = results.ToText();
        results.ExitCode.ShouldBe(0, text);
        var unit = results.Units.ShouldHaveSingleItem();
        unit.Verdict.ShouldBe(GateVerdict.Passed, text);
        var run = unit.Results.ShouldNotBeNull();

        // Detected after the window filled, healed exactly once, and the preflight ran again
        // after the heal — once at the gate's start, once after the heal.
        service.Heals.ShouldBe(1);
        service.Preflights.ShouldBe(2);
        var context = service.HealContexts.ShouldHaveSingleItem();
        context.Track.Name.ShouldBe("sqlserver");
        context.Heal.ShouldBe(1);
        context.Unit.ShouldBe(unit.Unit);
        context.Degradation.Cause.ShouldBe(DegradationCause.SlowTests);
        context.Degradation.SlowTests.Count.ShouldBe(3);

        run.Degradations.ShouldHaveSingleItem().Pace.ShouldNotBeNull().Factor.ShouldBeGreaterThan(10);
        var heal = run.TrackHeals.ShouldHaveSingleItem();
        heal.Track.ShouldBe("sqlserver");
        heal.Number.ShouldBe(1);
        heal.PaceBefore.ShouldNotBeNull().Observed.ShouldBe(degradedPace);
        heal.PaceBefore.Known.ShouldBe(known);
        // The evidence the heal worked: the next window ran at its known pace.
        heal.PaceAfter.ShouldNotBeNull().Observed.ShouldBe(known);
        heal.PaceAfter.Samples.ShouldBe(3);
        observer.Heals.ShouldHaveSingleItem().PaceAfter.ShouldNotBeNull();
        observer.Degradations.ShouldHaveSingleItem();

        // The three slow tests passed and stand; the five the dying lane never reached were
        // killed for the heal, resumed in their lane on a fresh worker, and passed — never
        // flakes, never a worker fault, never charged to a budget this run does not have.
        run.Tests.ShouldAllBe(t => t.Final.Succeeded);
        run.WorkerFaults.ShouldBeEmpty();
        run.StallKills.ShouldBeEmpty();
        run.PassedOnRetry.ShouldBeEmpty();
        run.Quarantine.ShouldBeEmpty();
        run.Tests.Count(t => t.AttemptCount == 1).ShouldBe(3);
        var resumed = run.Tests.Where(t => t.AttemptCount == 2).ToList();
        resumed.Count.ShouldBe(5);
        foreach (var test in resumed)
        {
            test.Attempts[0].HealInduced.ShouldBeTrue();
            test.Attempts[0].StallInduced.ShouldBeFalse();
            test.Attempts[0].Outcome.State.ShouldBe(WorkerTestState.Indeterminate);
            test.Attempts[0].Disposition.Kind.ShouldBe(DispositionKind.RetryInProcess);
            test.Attempts[1].Placement.ShouldBe(AttemptPlacement.SameProcess);
            test.Outcome.ShouldBe(RunOutcome.CleanPass);
        }

        // Discovery, the lane, the lane again after the heal: the resumed tests ran on a fresh
        // worker launched after the service was healed.
        factory.Launched.Count.ShouldBe(3);
        factory.Launched[2].Launch.Purpose.ShouldBe(WorkerPurpose.Lane);

        // Said in the log, the unit's report and the gate's line.
        lock (_log)
        {
            _log.ShouldContain(l => l.Contains("DEGRADED: the last 3 completed test(s) with a known duration each ran more than 10x it"));
            _log.ShouldContain(l => l.Contains("healing track sqlserver (heal 1 of 2)"));
            _log.ShouldContain(l => l.Contains("track sqlserver healed and its preflight passed"));
            _log.ShouldContain(l => l.Contains("HEALED track sqlserver (heal 1)"));
        }

        var report = RunReport.ToText(run);
        report.ShouldContain("Tracks healed");
        report.ShouldContain("sqlserver (heal 1)");
        RunReport.ToJson(run).ShouldContain("\"trackHeals\"");
        text.ShouldContain("1 track heal(s)");
    }

    [Fact]
    public async Task a_second_degradation_past_max_heals_aborts_the_unit_naming_the_track_and_other_units_keep_running()
    {
        var service = new Service();
        var slowTests = suite("Slow", 8);
        var fastTests = suite("Fast", 2);
        var slow = pacedBy(service, slowTests);
        var fast = new FakeWorkerFactory { Tests = fastTests, Outcome = (_, _, _) => WorkerTestState.Passed };

        var declaration = gate();
        // A heal that does not fix anything, bounded to one: the service stays slow after it.
        declaration.Tracks.Add(healable(service, maxHeals: 1, afterHeal: () => service.Degraded = true));
        declaration.Project(project("SlowSuite")).On("sqlserver").Supervise(supervise(knownDurations(slowTests)));
        declaration.Project(project("FastSuite")).Supervise(supervise(knownDurations(fastTests)));
        declaration.WorkerFactory = unit => unit.Project.Name == "SlowSuite" ? slow : fast;

        var results = await declaration.Run().WaitAsync(waitBudget);

        var text = results.ToText();
        results.ExitCode.ShouldBe(2, text);

        var aborted = results.Units.Single(u => u.Unit.Project.Name == "SlowSuite");
        aborted.Verdict.ShouldBe(GateVerdict.Indeterminate);
        var reason = aborted.Results.ShouldNotBeNull().AbortReason.ShouldNotBeNull();
        reason.ShouldContain("track sqlserver degraded again after being healed 1 time(s) this gate run (MaxHeals = 1)");
        reason.ShouldContain("the last 3 completed test(s)");

        // Healed once, degraded twice; the second degradation is what aborted.
        service.Heals.ShouldBe(1);
        aborted.Results.TrackHeals.Count.ShouldBe(1);
        aborted.Results.Degradations.Count.ShouldBe(2);

        // The unit on another track is untouched.
        results.Units.Single(u => u.Unit.Project.Name == "FastSuite").Verdict.ShouldBe(GateVerdict.Passed);
        text.ShouldContain("ABORTED track sqlserver degraded again");
    }

    [Fact]
    public async Task a_preflight_that_fails_after_a_heal_aborts_with_that_reason()
    {
        var service = new Service();
        var tests = suite("Slow", 6);
        var healed = false;

        var track = new Track("asb")
            .Check("the emulator answers", () =>
            {
                Interlocked.Increment(ref service.Preflights);
                if (healed) throw new TimeoutException("nothing answered at localhost:5672 within 5s");
            })
            .Heal(_ =>
            {
                Interlocked.Increment(ref service.Heals);
                service.Degraded = false;
                healed = true;
                return Task.CompletedTask;
            });

        var declaration = gate();
        declaration.Tracks.Add(track);
        declaration.Project(project("SlowSuite")).On("asb").Supervise(supervise(knownDurations(tests)));
        declaration.WorkerFactory = _ => pacedBy(service, tests);

        var results = await declaration.Run().WaitAsync(waitBudget);

        var unit = results.Units.ShouldHaveSingleItem();
        unit.Verdict.ShouldBe(GateVerdict.Indeterminate, results.ToText());
        var reason = unit.Results.ShouldNotBeNull().AbortReason.ShouldNotBeNull();
        reason.ShouldContain("track asb failed its preflight after being healed (heal 1)");
        reason.ShouldContain("nothing answered at localhost:5672");

        service.Heals.ShouldBe(1);
        service.Preflights.ShouldBe(2);
        // The heal ran, so it counts: nothing was recorded as healed, but the budget was spent.
        unit.Results.TrackHeals.ShouldBeEmpty();
        lock (_log) _log.ShouldContain(l => l.Contains("✗ track asb failed preflight after being healed"));
    }

    [Fact]
    public async Task a_track_with_no_heal_only_reports()
    {
        // Report, don't act: the same guardrail as a stall under StallAction.Report. The run
        // finishes slow and green, and says it was slow.
        var service = new Service();
        var tests = suite("Slow", 7);
        var observer = new RecordingObserver();

        var declaration = gate();
        declaration.Tracks.Add(new Track("sqlserver").Check("answers", () => Interlocked.Increment(ref service.Preflights)));
        declaration.Project(project("SlowSuite")).On("sqlserver").Supervise(supervise(knownDurations(tests), observer));
        var factory = pacedBy(service, tests);
        declaration.WorkerFactory = _ => factory;

        var results = await declaration.Run().WaitAsync(waitBudget);

        var unit = results.Units.ShouldHaveSingleItem();
        unit.Verdict.ShouldBe(GateVerdict.Passed, results.ToText());
        var run = unit.Results.ShouldNotBeNull();

        // Two windows of three slow tests, each reported; nothing killed, nothing healed, nothing retried.
        run.Degradations.Count.ShouldBe(2);
        observer.Degradations.Count.ShouldBe(2);
        run.TrackHeals.ShouldBeEmpty();
        observer.Heals.ShouldBeEmpty();
        run.Tests.ShouldAllBe(t => t.AttemptCount == 1);
        service.Preflights.ShouldBe(1);
        factory.Launched.Count.ShouldBe(2);

        lock (_log)
        {
            _log.ShouldContain(l => l.Contains("DEGRADED:"));
            _log.ShouldNotContain(l => l.Contains("PAUSING") || l.Contains("healing track"));
        }

        var report = RunReport.ToText(run);
        report.ShouldContain("Degraded (slow against the known durations; nothing healed it):");
        report.ShouldNotContain("Tracks healed");
        RunReport.ToJson(run).ShouldContain("\"degradations\"");
    }

    [Fact]
    public async Task a_first_run_with_no_known_durations_never_degrades()
    {
        // No history, no signal: there is nothing to be slow against. The heal is declared and
        // never asked for.
        var service = new Service();
        var tests = suite("Slow", 6);
        var observer = new RecordingObserver();

        var declaration = gate();
        declaration.Tracks.Add(healable(service));
        declaration.Project(project("SlowSuite")).On("sqlserver").Supervise(supervise(durations: null, observer));
        declaration.WorkerFactory = _ => pacedBy(service, tests);

        var results = await declaration.Run().WaitAsync(waitBudget);

        results.Units.ShouldHaveSingleItem().Verdict.ShouldBe(GateVerdict.Passed, results.ToText());
        service.Heals.ShouldBe(0);
        service.Preflights.ShouldBe(1);
        observer.Degradations.ShouldBeEmpty();
        var run = results.Units[0].Results.ShouldNotBeNull();
        run.Degradations.ShouldBeEmpty();
        run.TrackHeals.ShouldBeEmpty();
        run.Tests.ShouldAllBe(t => t.AttemptCount == 1);
    }

    [Fact]
    public async Task the_test_in_flight_when_the_run_degrades_is_retried_alone_and_its_batch_mates_resume_in_their_lane()
    {
        // Two lanes: lane 0 runs the slow class, lane 1 holds a test mid-flight. When the window
        // fills the kill lands on both — the held test was in flight, so it is retried alone in
        // a fresh process; the slow class's remaining tests were never reached, so they resume in
        // their lane. Driven on the supervisor directly, with the gate's seam stubbed.
        var service = new Service();
        var slow = suite("Slow", 6);
        var held = FakeWorkerFactory.InClass("Held", "in_flight");
        var tests = slow.Append(held).ToList();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new FakeWorkerFactory
        {
            Tests = tests,
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = async (uid, worker) =>
            {
                if (uid == held.Uid)
                {
                    if (worker.Launch.Purpose != WorkerPurpose.Lane) return;
                    heldStarted.TrySetResult();
                    await hold.Task;
                    return;
                }

                // The slow lane waits for the other lane to be mid-test before it starts running
                // slow, so the kill is guaranteed to land on a test in flight. Awaited, not
                // blocked: the lanes are started from one thread, and blocking it here would stop
                // the second lane from ever starting.
                await heldStarted.Task.WaitAsync(waitBudget);
                _time.Advance(service.Degraded ? degradedPace : known);
            }
        };

        var sink = new RecordingSink();
        var healed = new List<RunDegradation>();
        var supervisor = new Supervisor(factory)
        {
            Time = _time,
            MaxParallelWorkers = 2,
            KnownTestDurations = knownDurations(slow),
            DegradationWindow = 3,
            PublishToMonitor = true,
            MonitorSink = sink,
            OnDegraded = (degradation, _) =>
            {
                lock (healed) healed.Add(degradation);
                service.Degraded = false;
                return Task.FromResult(DegradationResponse.HealedTracks(new HealedTrack("sqlserver", 1)));
            }
        };

        var results = await supervisor.Run().WaitAsync(waitBudget);

        results.ExitCode.ShouldBe(0, RunReport.ToText(results));
        healed.ShouldHaveSingleItem().Cause.ShouldBe(DegradationCause.SlowTests);
        results.Tests.ShouldAllBe(t => t.Final.Succeeded);
        results.PassedOnRetry.ShouldBeEmpty();
        results.Quarantine.ShouldBeEmpty();
        results.WorkerFaults.ShouldBeEmpty();

        var inFlight = results.Tests.Single(t => t.Uid == held.Uid);
        inFlight.AttemptCount.ShouldBe(2);
        inFlight.Attempts[0].HealInduced.ShouldBeTrue();
        inFlight.Attempts[0].Disposition.Kind.ShouldBe(DispositionKind.RetryInFreshProcess);
        inFlight.Attempts[0].Disposition.Reason.ShouldContain("was in flight when the run degraded");
        inFlight.Attempts[1].Placement.ShouldBe(AttemptPlacement.IsolatedProcess);
        inFlight.Outcome.ShouldBe(RunOutcome.CleanPass);

        var resumed = results.Tests.Where(t => t.Uid.StartsWith("Slow.") && t.AttemptCount == 2).ToList();
        resumed.Count.ShouldBe(3);
        resumed.ShouldAllBe(t => t.Attempts[0].Disposition.Kind == DispositionKind.RetryInProcess);
        resumed.ShouldAllBe(t => t.Attempts[1].Placement == AttemptPlacement.SameProcess);

        // On the wire: one track_healed, with the pace before and after and the heal number,
        // announced before the run closed.
        var events = sink.Events;
        var wire = events.OfType<TrackHealed>().ShouldHaveSingleItem();
        wire.Track.ShouldBe("sqlserver");
        wire.Heal.ShouldBe(1);
        wire.Cause.ShouldBe("SlowTests");
        wire.PaceBeforeMs.ShouldBe((long)degradedPace.TotalMilliseconds);
        wire.KnownPaceMs.ShouldBe((long)known.TotalMilliseconds);
        wire.PaceAfterMs.ShouldBe((long)known.TotalMilliseconds);
        wire.Reason.ShouldContain("each ran more than 10x");
        var order = events.ToList();
        order.IndexOf(wire).ShouldBeLessThan(order.IndexOf(order.OfType<RunFinished>().Single()));
        events.OfType<WorkerFaulted>().ShouldBeEmpty();
    }

    [Fact]
    public async Task a_stall_is_healed_rather_than_escalated_when_a_handler_owns_the_response()
    {
        // The stall is still reported as a stall; what follows it is the heal, not the
        // StallAction ladder — the heal is the kill-and-retry-alone with the service recreated
        // in between. Once the handler says no, the next stall aborts like any other degradation.
        var service = new Service();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new RecordingObserver();

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.InClass("Suite", "hangs"), FakeWorkerFactory.InClass("Suite", "after")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (uid, worker) =>
            {
                if (uid != "Suite.hangs" || worker.Launch.Purpose != WorkerPurpose.Lane) return Task.CompletedTask;
                started.TrySetResult();
                return hold.Task;
            }
        };

        var supervisor = new Supervisor(factory)
        {
            Time = _time,
            StallThreshold = TimeSpan.FromSeconds(30),
            StallAction = StallAction.KillAndRetry,
            OnDegraded = (_, _) =>
            {
                Interlocked.Increment(ref service.Heals);
                return Task.FromResult(DegradationResponse.HealedTracks(new HealedTrack("rabbit", 1)));
            }
        };
        supervisor.AddObserver(observer);

        var run = supervisor.Run();
        await started.Task.WaitAsync(waitBudget);
        _time.Advance(TimeSpan.FromSeconds(31));

        var results = await run.WaitAsync(waitBudget);

        results.ExitCode.ShouldBe(0, RunReport.ToText(results));
        results.StalledTests.ShouldHaveSingleItem();
        // Healed, not stall-killed: the kill ledger is empty and the heal ledger has the entry.
        results.StallKills.ShouldBeEmpty();
        service.Heals.ShouldBe(1);
        var heal = results.TrackHeals.ShouldHaveSingleItem();
        heal.Cause.ShouldBe(DegradationCause.Stall);
        heal.Reason.ShouldContain("Suite.hangs stalled");
        // No known durations, so no pace either side — unmeasured, never zero.
        heal.PaceBefore.ShouldBeNull();
        heal.PaceAfter.ShouldBeNull();
        observer.Heals.ShouldHaveSingleItem();
        observer.Degradations.ShouldHaveSingleItem().Cause.ShouldBe(DegradationCause.Stall);

        var hangs = results.Tests.Single(t => t.Uid == "Suite.hangs");
        hangs.Attempts[0].HealInduced.ShouldBeTrue();
        hangs.Attempts[1].Placement.ShouldBe(AttemptPlacement.IsolatedProcess);
        hangs.Outcome.ShouldBe(RunOutcome.CleanPass);
        results.Tests.Single(t => t.Uid == "Suite.after").Attempts[1].Placement.ShouldBe(AttemptPlacement.SameProcess);
    }

    [Fact]
    public async Task a_heal_that_throws_aborts_the_run_rather_than_resuming_against_an_unknown_service()
    {
        var service = new Service();
        var tests = suite("Slow", 6);

        var supervisor = new Supervisor(pacedBy(service, tests))
        {
            Time = _time,
            KnownTestDurations = knownDurations(tests),
            DegradationWindow = 3,
            OnDegraded = (_, _) => throw new InvalidOperationException("docker compose exited 1")
        };

        var results = await supervisor.Run().WaitAsync(waitBudget);

        results.ExitCode.ShouldBe(2);
        results.AbortReason.ShouldNotBeNull().ShouldContain("Healing the run after it degraded failed: docker compose exited 1");
        results.TrackHeals.ShouldBeEmpty();
        results.Degradations.ShouldHaveSingleItem();
    }
}
