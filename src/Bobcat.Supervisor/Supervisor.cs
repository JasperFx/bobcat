using System.Diagnostics;
using JasperFx.Testing;
using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Resilience;
using Bobcat.Runtime;

namespace Bobcat.Supervisor;

/// <summary>
/// Runs a test suite across worker processes, applying the resilience policy at the one altitude
/// that can act on it — above the process boundary.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of issue #41 that <see cref="Disposition"/> was built for.
/// <c>RetryInFreshProcess</c> and running an <c>[Isolated]</c> test alone cannot be decided or
/// performed by the thing running inside the process that needs replacing; the supervisor owns
/// the processes, so it can.
/// </para>
/// <para>
/// It knows nothing about Gherkin. Anything that speaks MTP — Bobcat specs, xUnit v3, tUnit —
/// is a valid worker.
/// </para>
/// </remarks>
public sealed class Supervisor
{
    private readonly IWorkerFactory _factory;
    private readonly List<IFailurePolicy> _policies = new();
    private readonly List<ISupervisorObserver> _observers = new();

    private readonly List<string> _workerFaults = new();
    private readonly Dictionary<string, IRecyclableResource> _recyclable = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recyclings = new();

    // One slot per lane. Each slot is touched by exactly one task while a pass is in flight, and
    // the list is sized before any of them start, so the slots need no lock of their own.
    private readonly List<IWorkerClient?> _lanes = [];
    private readonly Dictionary<string, int> _laneOfTest = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    // What discovery called each test. Written once, before anything runs, and read only when a
    // worker gives us an outcome that has no name of its own.
    private readonly Dictionary<string, string> _discoveredNames = new(StringComparer.Ordinal);

    private int _workersLaunched;
    private long _launchTicks;
    private bool _lanesReleased;

    // Fixed for the duration of one run: the registered observers plus, when publishing is on,
    // the monitor's. Computed once so a run's notifications cannot change under it.
    private ISupervisorObserver[] _watching = [];
    private IReadOnlyDictionary<string, string>? _monitorEnvironment;

    public Supervisor(IWorkerFactory factory) => _factory = factory;

    /// <summary>Caps the retrying. Defaults to none, so retries are always an explicit choice.</summary>
    public RetryBudget RetryBudget { get; set; } = RetryBudget.None;

    /// <summary>
    /// How many worker processes may run concurrently. Defaults to 1 — a run is sequential
    /// unless someone asks for otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opt-in for the same reason retries are: parallelism changes what a suite means. Tests that
    /// share a database, a broker, or a fixed port pass alone and fail alongside each other, and
    /// turning that on by default would convert a working suite into a flaky one on upgrade.
    /// </para>
    /// <para>
    /// <see cref="WorkPlan"/> keeps each test class whole, which is what makes this safe for
    /// class-scoped state. It cannot protect against state shared <em>between</em> classes —
    /// a fixed port, or one database every class truncates. Those need per-worker environments.
    /// </para>
    /// </remarks>
    public int MaxParallelWorkers { get; set; } = 1;

    /// <summary>
    /// Overrides how tests are grouped into units that must stay in one process. Defaults to
    /// <see cref="WorkPlan.ClassOf"/>.
    /// </summary>
    public Func<WorkerTest, string>? PartitionKey { get; set; }

    /// <summary>
    /// Disposes the idle lane workers before any fresh-process retry runs, once no pending
    /// retry needs them. Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lane processes sit at their peak working set after the first pass, so a fresh-process
    /// retry otherwise means <c>workers + 1</c> full test hosts resident at once — enough to get
    /// a memory-constrained CI runner killed outright (observed twice on 16GB GitHub runners
    /// driving Wolverine's Marten suite, both times timestamped during a retry).
    /// </para>
    /// <para>
    /// The trade is stated rather than hidden: a same-process retry needs the lane that ran the
    /// test, so lanes are only released when nothing pending asks for one — and a policy that
    /// asks for <c>RetryInProcess</c> <em>after</em> the release is recorded in
    /// <see cref="SupervisorAttempt.Unsupported"/> rather than silently rerun in a process that
    /// no longer exists. Turn this on when your policy only ever retries in fresh processes.
    /// </para>
    /// </remarks>
    public bool ReleaseIdleLanes { get; set; }

    /// <summary>
    /// Keeps only the tests this run should own. Applied to the discovery list before anything
    /// is scheduled — an excluded test is never launched, never retried, never reported.
    /// </summary>
    /// <remarks>
    /// This is the supervisor-level equivalent of a <c>dotnet test --filter</c> expression: a CI
    /// target that skips a quarantined category, or a shard that owns one slice of a heavy suite,
    /// states that in code against <see cref="WorkerTest.Traits"/> and
    /// <see cref="WorkerTest.DisplayName"/>. Filtering here rather than on the worker's command
    /// line keeps the decision in one place for every front-end the supervisor can drive.
    /// </remarks>
    public Func<WorkerTest, bool>? TestFilter { get; set; }

    /// <summary>
    /// Previously observed per-test durations, keyed by uid, used to balance the lanes.
    /// </summary>
    /// <remarks>
    /// Optional, and a first run has none — then partitions are weighted by test count, which is
    /// a poor proxy. Measured on Wolverine's <c>PersistenceTests</c>: count-balanced lanes finished
    /// at 101.5s and 11.4s, so nearly a quarter of the fleet sat idle. The natural source is the
    /// committed ledger of issues #44 and #56; until that exists a caller can persist
    /// <c>WorkerOutcome.Duration</c> from the previous run itself.
    /// </remarks>
    public IReadOnlyDictionary<string, TimeSpan>? KnownTestDurations { get; set; }

    /// <summary>Progress, for a console or a log. Never required.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Publish this run to a Bobcat.Monitor console. Opt-in (same policy as
    /// <c>BobcatRunner.PublishToMonitor</c>): a unit test driving a supervisor must never
    /// probe a monitor that happens to be running on the box.
    /// </summary>
    /// <remarks>
    /// When on and a monitor answers, the supervisor owns the run bracket — one dashboard
    /// card for the whole run, with the true test total and the final counts including
    /// <c>Indeterminate</c> — and every worker inherits <c>BOBCAT_RUN_ID</c> +
    /// <c>BOBCAT_RUN_OWNER</c>, so worker scenario/step streams land on this run instead of
    /// each worker being its own card. When the monitor is absent, the run proceeds exactly
    /// as before; the probe costs one refused local connection.
    /// </remarks>
    public bool PublishToMonitor { get; set; }

    /// <summary>
    /// Where the run bracket goes when <see cref="PublishToMonitor"/> is on. Null means
    /// connect to the real monitor over HTTP; tests (or a custom transport) substitute a sink.
    /// </summary>
    public IMonitorEventSink? MonitorSink { get; set; }

    public Supervisor AddFailurePolicy(IFailurePolicy policy)
    {
        _policies.Add(policy);
        return this;
    }

    /// <summary>
    /// Watches the run as it happens — retry topology, lane lifecycle, recycles, worker deaths.
    /// See <see cref="ISupervisorObserver"/> for the contract.
    /// </summary>
    public Supervisor AddObserver(ISupervisorObserver observer)
    {
        _observers.Add(observer);
        return this;
    }

    /// <summary>
    /// Registers infrastructure the supervisor owns and may throw away between attempts.
    /// A worker cannot recycle the broker it is about to be replaced alongside, so these are
    /// hoisted to supervisor scope.
    /// </summary>
    public Supervisor AddRecyclableResource(IRecyclableResource resource)
    {
        _recyclable[resource.Name] = resource;
        return this;
    }

    /// <summary>Resources recycled during the run, in order. Reported: recycling is not free.</summary>
    public IReadOnlyList<string> Recyclings => _recyclings;

    /// <summary>
    /// Checks run ONCE before any worker is launched. This is the Playwright-never-renders case
    /// promoted to a first-class, test-independent stage: a broken environment aborts in seconds
    /// instead of after thousands of identical failures.
    /// </summary>
    public Preflight Preflight { get; } = new();

    /// <summary>
    /// Author-declared knowledge about which failures recover how — issue #44 layer 1.
    /// </summary>
    /// <remarks>
    /// Registered as <see cref="RecoveryHint"/>s rather than read from
    /// <see cref="RecoveryHintAttribute"/>s, because the supervisor drives the worker as a
    /// <em>process</em> and never loads the assembly the attributes are compiled into. Projecting
    /// a worker's own hints across the wire is the follow-on; until then a supervised run states
    /// its hints here, in the same place it states its budget.
    /// </remarks>
    public RecoveryHintSet RecoveryHints { get; } = new();

    private IFailurePolicy policy => new FailurePolicyChain(
        [.. _policies, new HintedFailurePolicy(RecoveryHints), new DefaultFailurePolicy()]);

    public async Task<SupervisorResults> Run(CancellationToken ct = default)
    {
        // Started before anything else so the run's wall clock accounts for the whole run —
        // preflight, discovery and the gaps between passes included. Anything narrower would
        // flatter the harness by measuring only the parts that are already in the report.
        var clock = Stopwatch.StartNew();

        // Connected before anything launches, because every worker — the discovery one
        // included — must inherit the grouping environment from its very first launch.
        var monitor = PublishToMonitor
            ? await SupervisorRunPublisher.TryStart(MonitorSink, _factory.Description)
            : null;
        _monitorEnvironment = monitor?.WorkerEnvironment;

        // The monitor publisher watches like any other observer — the run bracket and the
        // retry topology are the same kind of fact, and there is no reason for the supervisor
        // to know which of its watchers happens to be the monitor.
        _watching = monitor is null ? [.. _observers] : [.. _observers, monitor];

        try
        {
            var results = await run(monitor, ct);

            results.Duration = clock.Elapsed;
            results.WorkerLaunchTime = TimeSpan.FromTicks(Interlocked.Read(ref _launchTicks));

            // Posted for every run that produced results — including preflight failures and
            // empty filters — so a dashboard never shows a bracket that opened and never closed
            // unless the supervisor itself died.
            monitor?.RunFinished(results);
            return results;
        }
        finally
        {
            // A cancelled or thrown-through run posts no RunFinished — its heartbeats just
            // stop, and the monitor's orphan detection tells the truth about what happened.
            if (monitor is not null) await monitor.DisposeAsync();
        }
    }

    private async Task<SupervisorResults> run(SupervisorRunPublisher? monitor, CancellationToken ct)
    {
        var attempts = new Dictionary<string, List<SupervisorAttempt>>(StringComparer.Ordinal);
        string? abortReason = null;

        try
        {
            // Before any worker exists. Nothing downstream is meaningful if the environment is
            // broken, and finding that out per-test wastes the whole CI slot.
            var preflight = await runPreflight(ct);
            if (preflight is not null)
            {
                // The total is genuinely unknown — discovery never ran.
                monitor?.RunStarted(totalTests: null);

                return new SupervisorResults
                {
                    Tests = [],
                    AbortReason = preflight,
                    WorkersLaunched = 0
                };
            }

            var tests = await discover(ct);

            if (TestFilter is not null)
            {
                var discovered = tests.Count;
                tests = tests.Where(TestFilter).ToList();
                Log?.Invoke($"Filter kept {tests.Count} of {discovered} discovered test(s)");
            }

            // The bracket opens with the true post-filter total — something no single
            // worker knows.
            monitor?.RunStarted(tests.Count);

            if (tests.Count == 0)
            {
                return new SupervisorResults { Tests = [], WorkersLaunched = _workersLaunched };
            }

            var traits = tests.ToDictionary(t => t.Uid, t => t.Traits, StringComparer.Ordinal);

            // Kept so a test that never reports a result can still be named in the report — the
            // uid is a hash on some front-ends, and a hash makes triage a guessing game.
            foreach (var test in tests) _discoveredNames[test.Uid] = test.DisplayName;

            // Isolation is decided from discovery metadata, before anything runs. That is the
            // point of Q4 in the #43 spike: traits arrive early enough to plan scheduling.
            var isolated = tests.Where(t => isIsolated(t.Traits)).Select(t => t.Uid).ToList();
            var batched = tests.Where(t => !isIsolated(t.Traits)).ToList();

            Log?.Invoke($"{tests.Count} test(s): {batched.Count} batched, {isolated.Count} isolated");

            abortReason = await firstPass(batched, isolated, traits, attempts, ct);

            if (abortReason is null)
            {
                abortReason = await retryPasses(traits, attempts, ct);
            }
        }
        finally
        {
            await disposeLanes();
        }

        return new SupervisorResults
        {
            Tests = attempts
                .Select(pair => new TestReport
                {
                    Uid = pair.Key,
                    DisplayName = pair.Value[^1].Outcome.DisplayName,
                    Attempts = pair.Value
                })
                .OrderBy(t => t.DisplayName, StringComparer.Ordinal)
                .ToList(),
            AbortReason = abortReason,
            WorkersLaunched = _workersLaunched,
            WorkerFaults = _workerFaults,
            Recyclings = _recyclings
        };
    }

    private async Task<string?> runPreflight(CancellationToken ct)
    {
        // Recyclable infrastructure is supervisor-owned, so the supervisor is the only thing
        // that can check it before the run starts.
        Preflight.AddResourceChecks(_recyclable.Values);
        if (Preflight.IsEmpty) return null;

        var results = await Preflight.Run(ct);
        if (results.Succeeded()) return null;

        var description = Bobcat.Runtime.Preflight.Describe(results);
        Log?.Invoke(description);
        return description;
    }

    private async Task<IReadOnlyList<WorkerTest>> discover(CancellationToken ct)
    {
        // Discovery gets its own short-lived worker: it must not inherit state from, or leave
        // state in, a process that will go on to run tests.
        await using var worker = await launchWorker(WorkerLaunchContext.Discovery, ct);
        return await worker.Discover(ct);
    }

    private async Task<string?> firstPass(
        IReadOnlyList<WorkerTest> batched,
        IReadOnlyList<string> isolated,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> traits,
        Dictionary<string, List<SupervisorAttempt>> attempts,
        CancellationToken ct)
    {
        if (batched.Count > 0)
        {
            var plan = WorkPlan.Build(batched, MaxParallelWorkers, PartitionKey, KnownTestDurations);

            if (plan.Count > 1)
            {
                Log?.Invoke($"{plan.Count} lanes: " +
                            string.Join(", ", plan.Select(l => $"{l.Uids.Count} test(s) in {l.Partitions.Count} class(es)")));
            }

            // Sized before any lane starts, so each task owns its slot outright.
            lock (_gate)
            {
                while (_lanes.Count < plan.Count) _lanes.Add(null);
                foreach (var lane in plan)
                {
                    foreach (var uid in lane.Uids) _laneOfTest[uid] = lane.Index;
                }
            }

            var results = await Task.WhenAll(plan.Select(lane => runLane(lane.Index, lane.Uids, ct)));

            // Recording is deliberately serial and in lane order: it mutates the attempt history
            // and consults the policy, and a run's report should not depend on which worker
            // happened to finish first.
            foreach (var (index, result) in results.OrderBy(r => r.Index))
            {
                if (result.Crashed) invalidateLane(index, result.Fault!);
                recordFault(result);

                var abort = record(result, AttemptPlacement.Batched, traits, attempts);
                if (abort is not null) return abort;
            }
        }

        foreach (var uid in isolated)
        {
            ct.ThrowIfCancellationRequested();

            Log?.Invoke($"running alone: {uid}");
            var result = await runAlone(uid, WorkerPurpose.Isolated, ct);

            var abort = record(result, AttemptPlacement.IsolatedProcess, traits, attempts);
            if (abort is not null) return abort;
        }

        return null;
    }

    /// <summary>
    /// Keeps retrying while the policy asks for it and the budget allows. Each pass re-consults
    /// the policy with the latest outcome, so a test can change its mind about what it needs.
    /// </summary>
    private async Task<string?> retryPasses(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> traits,
        Dictionary<string, List<SupervisorAttempt>> attempts,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var sameProcess = new List<string>();
            var freshProcess = new List<string>();
            var afterRecycle = new List<(string Uid, IReadOnlyList<string> Resources)>();

            foreach (var (uid, history) in attempts)
            {
                var latest = history[^1];
                if (latest.Succeeded) continue;
                if (!latest.Disposition.IsRetry || latest.Unsupported is not null) continue;

                if (latest.Disposition.Kind == DispositionKind.RetryAfterRecycle)
                {
                    afterRecycle.Add((uid, latest.Disposition.Resources));
                }
                else if (latest.Disposition.Kind == DispositionKind.RetryInFreshProcess ||
                         isIsolated(traitsFor(traits, uid)))
                {
                    freshProcess.Add(uid);
                }
                else
                {
                    sameProcess.Add(uid);
                }

                // Announced here rather than from record(...): by this point the budget and the
                // resolve step have had their say, so a retry that was requested and not honoured
                // never reaches a watcher as though it were about to happen. The attempt number
                // is the supervisor's — the worker that runs it counts from one, whichever
                // process it lands in.
                notify(observer => observer.RetryScheduled(uid, history.Count + 1, latest.Disposition));
            }

            if (sameProcess.Count == 0 && freshProcess.Count == 0 && afterRecycle.Count == 0) return null;

            // Nothing pending needs a warm lane, so stop paying for them: every retry from here
            // launches its own process, and idle lanes at peak working set are what pushes a
            // memory-constrained runner over the edge.
            if (ReleaseIdleLanes && !_lanesReleased && sameProcess.Count == 0)
            {
                Log?.Invoke("releasing idle lane worker(s) before fresh-process retries");
                await disposeLanes();
                _lanesReleased = true;
            }

            if (sameProcess.Count > 0)
            {
                Log?.Invoke($"retrying {sameProcess.Count} test(s) in place");

                // Back to the lane the test already ran in, not merely to some warm process. A
                // class's static state lives in the process that ran it, so retrying elsewhere
                // would be a fresh-process retry wearing a same-process label.
                var byLane = sameProcess
                    .GroupBy(uid => _laneOfTest.TryGetValue(uid, out var lane) ? lane : 0)
                    .OrderBy(g => g.Key)
                    .ToList();

                var results = await Task.WhenAll(byLane.Select(g => runLane(g.Key, g.ToList(), ct)));

                foreach (var (index, result) in results.OrderBy(r => r.Index))
                {
                    if (result.Crashed) invalidateLane(index, result.Fault!);
                    recordFault(result);

                    var abort = record(result, AttemptPlacement.SameProcess, traits, attempts);
                    if (abort is not null) return abort;
                }
            }

            foreach (var uid in freshProcess)
            {
                ct.ThrowIfCancellationRequested();

                Log?.Invoke($"retrying alone in a fresh process: {uid}");
                var result = await runAlone(uid, WorkerPurpose.Isolated, ct);

                var abort = record(result, AttemptPlacement.IsolatedProcess, traits, attempts);
                if (abort is not null) return abort;
            }

            foreach (var (uid, resources) in afterRecycle)
            {
                ct.ThrowIfCancellationRequested();

                // Recycle FIRST, then run alone in a fresh process. Reusing the shared worker
                // would leave it connected to the broker we just threw away.
                var recycleFailure = await recycle(resources, ct);
                if (recycleFailure is not null) return recycleFailure;

                Log?.Invoke($"retrying after recycling [{string.Join(", ", resources)}]: {uid}");
                var result = await runAlone(uid, WorkerPurpose.Recycled, ct);

                var abort = record(result, AttemptPlacement.RecycledProcess, traits, attempts);
                if (abort is not null) return abort;
            }
        }
    }

    /// <summary>A dedicated process running exactly one test, then thrown away.</summary>
    private async Task<WorkerRunResult> runAlone(string uid, WorkerPurpose purpose, CancellationToken ct)
    {
        // Lane 0: these never run while the pool is running, so reusing the first slot's
        // per-worker resources cannot collide with anything.
        await using var worker = await launchWorker(new WorkerLaunchContext(0, purpose), ct);
        var result = await worker.Run([uid], ct);
        recordFault(result);
        return result;
    }

    /// <summary>
    /// Records outcomes and asks the policy what to do next. Returns an abort reason when a
    /// policy said to stop the whole run.
    /// </summary>
    private string? record(
        WorkerRunResult result,
        AttemptPlacement placement,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> traits,
        Dictionary<string, List<SupervisorAttempt>> attempts)
    {
        foreach (var reported in result.Outcomes)
        {
            var outcome = named(reported);

            if (!attempts.TryGetValue(outcome.Uid, out var history))
            {
                history = [];
                attempts[outcome.Uid] = history;
            }

            var attemptNumber = history.Count + 1;
            var testTraits = traitsFor(traits, outcome.Uid);

            var decided = policy.Decide(new AttemptContext
            {
                TestId = outcome.Uid,
                Title = outcome.DisplayName,
                AttemptNumber = attemptNumber,
                Succeeded = outcome.Succeeded,
                FailureLevel = failureLevelFor(outcome),
                Exception = exceptionFor(outcome),
                // Set explicitly: deriving it from ExceptionFor's stand-in would report
                // WorkerFailureException, which describes our wrapper rather than the failure.
                Failure = FailureSignature.FromReportedType(outcome.ErrorType, outcome.ErrorMessage),
                Traits = testTraits,
                RetriesAvailable = RetryBudget.CanRetry(outcome.Uid, testTraits),
                AttemptsAllowed = RetryBudget.AttemptsAllowedFor(testTraits)
            }) ?? Disposition.FailAndContinue("failed");

            var (effective, unsupported) = resolve(decided, outcome.Uid, testTraits);

            var recorded = new SupervisorAttempt(attemptNumber, outcome, placement, effective)
            {
                Unsupported = unsupported
            };

            history.Add(recorded);
            notify(observer => observer.AttemptRecorded(outcome.Uid, recorded));

            if (effective.Kind == DispositionKind.AbortRun) return effective.Reason;
        }

        return null;
    }

    /// <summary>
    /// Gives an outcome the name discovery knew it by, when the worker supplied none of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A synthesized <see cref="WorkerTestState.Indeterminate"/> outcome carries the uid as its
    /// display name, because the only thing the client knows about a test nobody reported is the
    /// uid it asked for. On front-ends whose uid is a hash — an environment-dependent xUnit theory
    /// identity, for instance — that report names a 64-character hash and triage becomes a
    /// guessing game. The supervisor ran discovery, so it is the one thing in the run that still
    /// has the human-readable name.
    /// </para>
    /// <para>
    /// The condition is "this outcome has no name but its uid", not "this outcome is
    /// indeterminate": it is a fallback for a missing name, and it never overwrites a name a
    /// worker actually reported.
    /// </para>
    /// </remarks>
    private WorkerOutcome named(WorkerOutcome outcome)
        => outcome.DisplayName == outcome.Uid &&
           _discoveredNames.TryGetValue(outcome.Uid, out var discovered) &&
           discovered != outcome.Uid
            ? outcome with { DisplayName = discovered }
            : outcome;

    /// <summary>
    /// Turns a policy's wish into what will actually happen. Anything not honoured is recorded
    /// with its reason rather than silently downgraded — a report that implies a retry which
    /// never happened is worse than no report.
    /// </summary>
    private (Disposition Effective, string? Unsupported) resolve(
        Disposition decided, string uid, IReadOnlyDictionary<string, string> traits)
    {
        if (!decided.IsRetry) return (decided, null);

        if (decided.Kind == DispositionKind.RetryInProcess && _lanesReleased)
        {
            // The process that ran the test is gone; rerunning "in process" elsewhere would be
            // a fresh-process retry wearing a same-process label.
            return (decided,
                "RetryInProcess was requested after the lane workers were released " +
                "(ReleaseIdleLanes) — this attempt was NOT retried.");
        }

        if (decided.Kind == DispositionKind.RetryAfterRecycle)
        {
            // Naming a resource nobody registered is a wiring mistake, and silently retrying
            // without recycling would hide it behind an ordinary flaky-test failure.
            var unknown = decided.Resources.Where(r => !_recyclable.ContainsKey(r)).ToList();
            if (unknown.Count > 0)
            {
                return (decided,
                    $"RetryAfterRecycle names [{string.Join(", ", unknown)}], which " +
                    (unknown.Count == 1 ? "is" : "are") + " not registered with the supervisor — " +
                    "this attempt was NOT retried. Register with AddRecyclableResource(...).");
            }
        }

        if (RetryBudget.TryConsume(uid, traits, out var denial)) return (decided, null);

        return (Disposition.FailAndContinue($"a retry was requested ({decided.Reason}) but {denial}"), null);
    }

    private static FailureLevel failureLevelFor(WorkerOutcome outcome) => outcome.State switch
    {
        WorkerTestState.Passed or WorkerTestState.Skipped => FailureLevel.None,
        WorkerTestState.Failed => FailureLevel.Assertion,
        _ => FailureLevel.Critical
    };

    private static Exception? exceptionFor(WorkerOutcome outcome)
        => outcome.Succeeded
            ? null
            : new WorkerFailureException(outcome.ErrorType, outcome.ErrorMessage ?? outcome.State.ToString());

    private static bool isIsolated(IReadOnlyDictionary<string, string> traits)
        => traits.TryGetValue(ResilienceTags.Isolated, out var value) &&
           value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> traitsFor(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> traits, string uid)
        => traits.TryGetValue(uid, out var found)
            ? found
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Throws the named resources away and stands fresh ones up. A recycle that fails is
    /// catastrophic by nature — the retry would run against infrastructure in an unknown state,
    /// so it is better to stop than to produce a result nobody can trust.
    /// </summary>
    private async Task<string?> recycle(IReadOnlyList<string> resources, CancellationToken ct)
    {
        foreach (var name in resources)
        {
            if (!_recyclable.TryGetValue(name, out var resource)) continue;

            Log?.Invoke($"recycling {name}");

            try
            {
                await resource.Recycle(ct);
                _recyclings.Add(name);
                notify(observer => observer.ResourceRecycled(name));
            }
            catch (Exception e)
            {
                // Reported as an abort rather than thrown, so everything already learned this
                // run survives. A thrown exception here would discard every result collected
                // so far, which is the opposite of useful when infrastructure breaks late.
                return $"Recycling the resource '{name}' failed, so the retry would run against " +
                       $"infrastructure in an unknown state: {e.Message}";
            }
        }

        return null;
    }

    /// <summary>Keeps a worker's dying words so the report can explain an indeterminate result.</summary>
    private void recordFault(WorkerRunResult result)
    {
        if (result.Fault is null) return;

        _workerFaults.Add(result.Fault);
        notify(observer => observer.WorkerFaulted(result.Fault));
    }

    /// <summary>
    /// Fans one callback out to every watcher. An observer that throws is logged and stepped
    /// over: a dashboard, a log sink or a metrics push must not be able to fail a test run.
    /// </summary>
    private void notify(Action<ISupervisorObserver> callback)
    {
        foreach (var observer in _watching)
        {
            try
            {
                callback(observer);
            }
            catch (Exception e)
            {
                Log?.Invoke($"a supervisor observer threw and was ignored: {e.Message}");
            }
        }
    }

    /// <summary>Runs one lane's tests in that lane's own long-lived worker.</summary>
    private async Task<(int Index, WorkerRunResult Result)> runLane(
        int index, IReadOnlyList<string> uids, CancellationToken ct)
    {
        var worker = await laneWorker(index, ct);

        notify(observer => observer.LaneStarted(index, uids));
        var result = await worker.Run(uids, ct);
        notify(observer => observer.LaneFinished(index, result));

        return (index, result);
    }

    private async Task<IWorkerClient> laneWorker(int index, CancellationToken ct)
    {
        lock (_gate)
        {
            while (_lanes.Count <= index) _lanes.Add(null);
            if (_lanes[index] is { } existing) return existing;
        }

        // Launched outside the lock — starting a process is slow, and every lane launches its
        // own at the same moment. Only this lane's task reaches this line for this index, so
        // there is no race to lose.
        var worker = await launchWorker(new WorkerLaunchContext(index, WorkerPurpose.Lane), ct);

        lock (_gate) _lanes[index] = worker;
        return worker;
    }

    /// <summary>
    /// Drops a lane's worker after it died, so the next retry in that lane gets a live process
    /// instead of failing against a corpse.
    /// </summary>
    private void invalidateLane(int index, string fault)
    {
        Log?.Invoke($"worker for lane {index} died ({fault}); a replacement will be launched if needed");

        IWorkerClient? dead;
        lock (_gate)
        {
            dead = index < _lanes.Count ? _lanes[index] : null;
            if (index < _lanes.Count) _lanes[index] = null;
        }

        if (dead is not null) _ = dead.DisposeAsync().AsTask();
    }

    private async Task disposeLanes()
    {
        IWorkerClient?[] workers;
        lock (_gate)
        {
            workers = _lanes.ToArray();
            _lanes.Clear();
        }

        foreach (var worker in workers)
        {
            if (worker is not null) await worker.DisposeAsync();
        }
    }

    private async Task<IWorkerClient> launchWorker(WorkerLaunchContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _workersLaunched);

        if (_monitorEnvironment is not null)
        {
            context = context with { Environment = _monitorEnvironment };
        }

        // Timed because it is the harness's own cost, and the harness is what decides how many
        // processes a run starts. Accumulated rather than measured as a span: lanes launch at the
        // same moment, so there is no single interval to measure.
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await _factory.Launch(context, ct);
        }
        finally
        {
            Interlocked.Add(ref _launchTicks, Stopwatch.GetElapsedTime(started).Ticks);
        }
    }
}
