using Bobcat.Engine;
using Bobcat.Resilience;

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

    private IFailurePolicy Policy => new FailurePolicyChain([.. _policies, new DefaultFailurePolicy()]);

    public async Task<SupervisorResults> Run(CancellationToken ct = default)
    {
        var attempts = new Dictionary<string, List<SupervisorAttempt>>(StringComparer.Ordinal);
        string? abortReason = null;

        try
        {
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
            WorkersLaunched = _workersLaunched
        };
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

            foreach (var (uid, history) in attempts)
            {
                var latest = history[^1];
                if (latest.Succeeded) continue;
                if (!latest.Disposition.IsRetry || latest.Unsupported is not null) continue;

                if (latest.Disposition.Kind == DispositionKind.RetryInFreshProcess ||
                    IsIsolated(TraitsFor(traits, uid)))
                {
                    freshProcess.Add(uid);
                }
                else
                {
                    sameProcess.Add(uid);
                }
            }

            if (sameProcess.Count == 0 && freshProcess.Count == 0) return null;

            if (sameProcess.Count > 0)
            {
                Log?.Invoke($"retrying {sameProcess.Count} test(s) in the shared worker");

                var worker = await SharedWorker(ct);
                var result = await worker.Run(sameProcess, ct);
                if (result.Crashed) InvalidateSharedWorker(result.Fault!);

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
        }
    }

    /// <summary>A dedicated process running exactly one test, then thrown away.</summary>
    private async Task<WorkerRunResult> RunAlone(string uid, CancellationToken ct)
    {
        await using var worker = await LaunchWorker(ct);
        return await worker.Run([uid], ct);
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
            // Recycling needs supervisor-owned IRecyclableResource — issue #41 build-order
            // step 4, not built yet.
            return (decided,
                $"RetryAfterRecycle({string.Join(", ", decided.Resources)}) needs supervisor-owned " +
                "recyclable resources, which are not built yet — this attempt was NOT retried");
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
