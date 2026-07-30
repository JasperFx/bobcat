using Bobcat.Engine;
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

    private readonly List<string> _workerFaults = new();
    private readonly Dictionary<string, IRecyclableResource> _recyclable = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recyclings = new();

    private IWorkerClient? _sharedWorker;
    private int _workersLaunched;

    public Supervisor(IWorkerFactory factory) => _factory = factory;

    /// <summary>Caps the retrying. Defaults to none, so retries are always an explicit choice.</summary>
    public RetryBudget RetryBudget { get; set; } = RetryBudget.None;

    /// <summary>Progress, for a console or a log. Never required.</summary>
    public Action<string>? Log { get; set; }

    public Supervisor AddFailurePolicy(IFailurePolicy policy)
    {
        _policies.Add(policy);
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

    private IFailurePolicy Policy => new FailurePolicyChain(
        [.. _policies, new HintedFailurePolicy(RecoveryHints), new DefaultFailurePolicy()]);

    public async Task<SupervisorResults> Run(CancellationToken ct = default)
    {
        var attempts = new Dictionary<string, List<SupervisorAttempt>>(StringComparer.Ordinal);
        string? abortReason = null;

        try
        {
            // Before any worker exists. Nothing downstream is meaningful if the environment is
            // broken, and finding that out per-test wastes the whole CI slot.
            var preflight = await RunPreflight(ct);
            if (preflight is not null)
            {
                return new SupervisorResults
                {
                    Tests = [],
                    AbortReason = preflight,
                    WorkersLaunched = 0
                };
            }

            var tests = await Discover(ct);
            if (tests.Count == 0)
            {
                return new SupervisorResults { Tests = [], WorkersLaunched = _workersLaunched };
            }

            var traits = tests.ToDictionary(t => t.Uid, t => t.Traits, StringComparer.Ordinal);

            // Isolation is decided from discovery metadata, before anything runs. That is the
            // point of Q4 in the #43 spike: traits arrive early enough to plan scheduling.
            var isolated = tests.Where(t => IsIsolated(t.Traits)).Select(t => t.Uid).ToList();
            var batched = tests.Where(t => !IsIsolated(t.Traits)).Select(t => t.Uid).ToList();

            Log?.Invoke($"{tests.Count} test(s): {batched.Count} batched, {isolated.Count} isolated");

            abortReason = await FirstPass(batched, isolated, traits, attempts, ct);

            if (abortReason is null)
            {
                abortReason = await RetryPasses(traits, attempts, ct);
            }
        }
        finally
        {
            if (_sharedWorker is not null) await _sharedWorker.DisposeAsync();
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

    private async Task<string?> RunPreflight(CancellationToken ct)
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

    private async Task<IReadOnlyList<WorkerTest>> Discover(CancellationToken ct)
    {
        // Discovery gets its own short-lived worker: it must not inherit state from, or leave
        // state in, a process that will go on to run tests.
        await using var worker = await LaunchWorker(ct);
        return await worker.Discover(ct);
    }

    private async Task<string?> FirstPass(
        IReadOnlyList<string> batched,
        IReadOnlyList<string> isolated,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> traits,
        Dictionary<string, List<SupervisorAttempt>> attempts,
        CancellationToken ct)
    {
        if (batched.Count > 0)
        {
            var worker = await SharedWorker(ct);
            var result = await worker.Run(batched, ct);
            if (result.Crashed) InvalidateSharedWorker(result.Fault!);
            RecordFault(result);

            var abort = Record(result, AttemptPlacement.Batched, traits, attempts);
            if (abort is not null) return abort;
        }

        foreach (var uid in isolated)
        {
            ct.ThrowIfCancellationRequested();

            Log?.Invoke($"running alone: {uid}");
            var result = await RunAlone(uid, ct);

            var abort = Record(result, AttemptPlacement.IsolatedProcess, traits, attempts);
            if (abort is not null) return abort;
        }

        return null;
    }

    /// <summary>
    /// Keeps retrying while the policy asks for it and the budget allows. Each pass re-consults
    /// the policy with the latest outcome, so a test can change its mind about what it needs.
    /// </summary>
    private async Task<string?> RetryPasses(
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
                         IsIsolated(TraitsFor(traits, uid)))
                {
                    freshProcess.Add(uid);
                }
                else
                {
                    sameProcess.Add(uid);
                }
            }

            if (sameProcess.Count == 0 && freshProcess.Count == 0 && afterRecycle.Count == 0) return null;

            if (sameProcess.Count > 0)
            {
                Log?.Invoke($"retrying {sameProcess.Count} test(s) in the shared worker");

                var worker = await SharedWorker(ct);
                var result = await worker.Run(sameProcess, ct);
                if (result.Crashed) InvalidateSharedWorker(result.Fault!);
                RecordFault(result);

                var abort = Record(result, AttemptPlacement.SameProcess, traits, attempts);
                if (abort is not null) return abort;
            }

            foreach (var uid in freshProcess)
            {
                ct.ThrowIfCancellationRequested();

                Log?.Invoke($"retrying alone in a fresh process: {uid}");
                var result = await RunAlone(uid, ct);

                var abort = Record(result, AttemptPlacement.IsolatedProcess, traits, attempts);
                if (abort is not null) return abort;
            }

            foreach (var (uid, resources) in afterRecycle)
            {
                ct.ThrowIfCancellationRequested();

                // Recycle FIRST, then run alone in a fresh process. Reusing the shared worker
                // would leave it connected to the broker we just threw away.
                var recycleFailure = await Recycle(resources, ct);
                if (recycleFailure is not null) return recycleFailure;

                Log?.Invoke($"retrying after recycling [{string.Join(", ", resources)}]: {uid}");
                var result = await RunAlone(uid, ct);

                var abort = Record(result, AttemptPlacement.RecycledProcess, traits, attempts);
                if (abort is not null) return abort;
            }
        }
    }

    /// <summary>A dedicated process running exactly one test, then thrown away.</summary>
    private async Task<WorkerRunResult> RunAlone(string uid, CancellationToken ct)
    {
        await using var worker = await LaunchWorker(ct);
        var result = await worker.Run([uid], ct);
        RecordFault(result);
        return result;
    }

    /// <summary>
    /// Records outcomes and asks the policy what to do next. Returns an abort reason when a
    /// policy said to stop the whole run.
    /// </summary>
    private string? Record(
        WorkerRunResult result,
        AttemptPlacement placement,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> traits,
        Dictionary<string, List<SupervisorAttempt>> attempts)
    {
        foreach (var outcome in result.Outcomes)
        {
            if (!attempts.TryGetValue(outcome.Uid, out var history))
            {
                history = [];
                attempts[outcome.Uid] = history;
            }

            var attemptNumber = history.Count + 1;
            var testTraits = TraitsFor(traits, outcome.Uid);

            var decided = Policy.Decide(new AttemptContext
            {
                TestId = outcome.Uid,
                Title = outcome.DisplayName,
                AttemptNumber = attemptNumber,
                Succeeded = outcome.Succeeded,
                FailureLevel = FailureLevelFor(outcome),
                Exception = ExceptionFor(outcome),
                // Set explicitly: deriving it from ExceptionFor's stand-in would report
                // WorkerFailureException, which describes our wrapper rather than the failure.
                Failure = FailureSignature.FromReportedType(outcome.ErrorType, outcome.ErrorMessage),
                Traits = testTraits,
                RetriesAvailable = RetryBudget.CanRetry(outcome.Uid, testTraits),
                AttemptsAllowed = RetryBudget.AttemptsAllowedFor(testTraits)
            }) ?? Disposition.FailAndContinue("failed");

            var (effective, unsupported) = Resolve(decided, outcome.Uid, testTraits);

            history.Add(new SupervisorAttempt(attemptNumber, outcome, placement, effective)
            {
                Unsupported = unsupported
            });

            if (effective.Kind == DispositionKind.AbortRun) return effective.Reason;
        }

        return null;
    }

    /// <summary>
    /// Turns a policy's wish into what will actually happen. Anything not honoured is recorded
    /// with its reason rather than silently downgraded — a report that implies a retry which
    /// never happened is worse than no report.
    /// </summary>
    private (Disposition Effective, string? Unsupported) Resolve(
        Disposition decided, string uid, IReadOnlyDictionary<string, string> traits)
    {
        if (!decided.IsRetry) return (decided, null);

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

    private static FailureLevel FailureLevelFor(WorkerOutcome outcome) => outcome.State switch
    {
        WorkerTestState.Passed or WorkerTestState.Skipped => FailureLevel.None,
        WorkerTestState.Failed => FailureLevel.Assertion,
        _ => FailureLevel.Critical
    };

    private static Exception? ExceptionFor(WorkerOutcome outcome)
        => outcome.Succeeded
            ? null
            : new WorkerFailureException(outcome.ErrorType, outcome.ErrorMessage ?? outcome.State.ToString());

    private static bool IsIsolated(IReadOnlyDictionary<string, string> traits)
        => traits.TryGetValue(ResilienceTags.Isolated, out var value) &&
           value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> TraitsFor(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> traits, string uid)
        => traits.TryGetValue(uid, out var found)
            ? found
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Throws the named resources away and stands fresh ones up. A recycle that fails is
    /// catastrophic by nature — the retry would run against infrastructure in an unknown state,
    /// so it is better to stop than to produce a result nobody can trust.
    /// </summary>
    private async Task<string?> Recycle(IReadOnlyList<string> resources, CancellationToken ct)
    {
        foreach (var name in resources)
        {
            if (!_recyclable.TryGetValue(name, out var resource)) continue;

            Log?.Invoke($"recycling {name}");

            try
            {
                await resource.Recycle(ct);
                _recyclings.Add(name);
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
    private void RecordFault(WorkerRunResult result)
    {
        if (result.Fault is not null) _workerFaults.Add(result.Fault);
    }

    private async Task<IWorkerClient> SharedWorker(CancellationToken ct)
        => _sharedWorker ??= await LaunchWorker(ct);

    /// <summary>
    /// Drops a worker that died, so the next same-process retry gets a live one instead of
    /// failing against a corpse.
    /// </summary>
    private void InvalidateSharedWorker(string fault)
    {
        Log?.Invoke($"the shared worker died ({fault}); a replacement will be launched if needed");

        var dead = _sharedWorker;
        _sharedWorker = null;
        if (dead is not null) _ = dead.DisposeAsync().AsTask();
    }

    private async Task<IWorkerClient> LaunchWorker(CancellationToken ct)
    {
        _workersLaunched++;
        return await _factory.Launch(ct);
    }
}
