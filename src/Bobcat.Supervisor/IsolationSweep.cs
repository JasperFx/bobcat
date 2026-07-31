using System.Collections.Concurrent;

namespace Bobcat.Supervisor;

/// <summary>How much of the suite runs together in one sweep process.</summary>
public enum SweepGranularity
{
    /// <summary>
    /// One process per test class. Matches the partitioning contract the supervisor actually
    /// guarantees, so it answers "is this suite safe to split across workers".
    /// </summary>
    PerClass,

    /// <summary>
    /// One process per test. Strictly finer: it additionally catches ordering *within* a class,
    /// which <see cref="PerClass"/> cannot see by construction. Costs one process per test.
    /// </summary>
    PerTest
}

/// <summary>What the sweep concluded about one test.</summary>
public enum SweepVerdict
{
    /// <summary>
    /// Passed with the suite, failed on its own. The bug this exists to find: the test was only
    /// ever passing because something else ran first.
    /// </summary>
    OrderDependent,

    /// <summary>
    /// Failed with the suite, passed on its own. Something else in the suite is corrupting it —
    /// the same defect seen from the other end.
    /// </summary>
    InterferenceVictim,

    /// <summary>Failed both ways. Ordinary red, and nothing to do with isolation.</summary>
    FailedInBoth,

    /// <summary>
    /// Failed during the concurrent sweep but passed the confirmation run. The failure came from
    /// the sweep's own conditions — a per-worker database, or contention between sweep processes —
    /// rather than from ordering. A confound, deliberately reported rather than counted.
    /// </summary>
    EnvironmentSensitive
}

/// <summary>One test the sweep has something to say about. Tests that behaved are not findings.</summary>
public sealed record SweepFinding(string Uid, string DisplayName, SweepVerdict Verdict)
{
    /// <summary>The group this test was swept in — its class, or itself under PerTest.</summary>
    public string Partition { get; init; } = "";

    /// <summary>Why it failed when run in isolation, when it did.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// The outcome of a sweep. <see cref="OrderDependent"/> is the answer; everything else is context
/// for reading it.
/// </summary>
public sealed record SweepResults
{
    public required IReadOnlyList<SweepFinding> Findings { get; init; }

    /// <summary>How many tests the sweep saw. A zero here means the sweep did nothing.</summary>
    public required int Discovered { get; init; }

    /// <summary>How many processes the isolation phase ran — classes, or tests.</summary>
    public required int Partitions { get; init; }

    /// <summary>Tests that failed in the baseline run of the whole suite.</summary>
    public required int BaselineFailures { get; init; }

    /// <summary>Set when the sweep could not complete, e.g. the baseline worker crashed.</summary>
    public string? AbortReason { get; init; }

    public IReadOnlyList<SweepFinding> OrderDependent =>
        [.. Findings.Where(f => f.Verdict == SweepVerdict.OrderDependent)];

    public IReadOnlyList<SweepFinding> InterferenceVictims =>
        [.. Findings.Where(f => f.Verdict == SweepVerdict.InterferenceVictim)];

    public IReadOnlyList<SweepFinding> FailedInBoth =>
        [.. Findings.Where(f => f.Verdict == SweepVerdict.FailedInBoth)];

    public IReadOnlyList<SweepFinding> EnvironmentSensitive =>
        [.. Findings.Where(f => f.Verdict == SweepVerdict.EnvironmentSensitive)];

    /// <summary>Nothing to fix: no order-dependence and no victims.</summary>
    public bool IsClean => OrderDependent.Count == 0 && InterferenceVictims.Count == 0;
}

/// <summary>
/// Runs every test alone and diffs the result against a run of the whole suite, to find tests that
/// only pass because something else ran first.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a mode of <see cref="Supervisor"/>. The supervisor runs <c>[Isolated]</c> tests
/// one at a time on purpose — those tests are isolated *because* they do not tolerate company, so
/// running several at once would defeat the point. A sweep is the opposite case: isolation is the
/// instrument rather than an accommodation, every test needs it, and it is only affordable if the
/// processes overlap.
/// </para>
/// <para>
/// <b>The failure mode of this class is reporting zero.</b> A sweep that silently swept nothing,
/// or whose isolation was not isolating, looks exactly like a clean suite. That is why
/// <see cref="SweepResults.Discovered"/> and <see cref="SweepResults.Partitions"/> are reported
/// alongside the findings, and why the tests include a planted order-dependent test rather than
/// only asserting that clean suites come back clean.
/// </para>
/// <para>
/// The confirmation pass is not optional either. Sweep workers may be pointed at per-worker
/// resources via <c>MtpWorkerFactory.EnvironmentFor</c>, and a suite that bakes a database name
/// into an assertion then fails for reasons that have nothing to do with ordering. Every failure
/// is therefore re-run alone, serially, on lane 0 before it is called order-dependent — see
/// <see cref="SweepVerdict.EnvironmentSensitive"/>.
/// </para>
/// </remarks>
public sealed class IsolationSweep
{
    private readonly IWorkerFactory _factory;

    public IsolationSweep(IWorkerFactory factory) => _factory = factory;

    /// <summary>How many isolation processes run at once. One means a serial sweep.</summary>
    public int MaxParallelWorkers { get; set; } = 1;

    /// <summary>
    /// Defaults to <see cref="SweepGranularity.PerClass"/> — cheaper, and it matches what the
    /// supervisor actually guarantees about partitioning.
    /// </summary>
    public SweepGranularity Granularity { get; set; } = SweepGranularity.PerClass;

    /// <summary>
    /// Overrides how tests are grouped under <see cref="SweepGranularity.PerClass"/>, for a suite
    /// whose real coupling is not its class. Same seam as <c>Supervisor.PartitionKey</c>.
    /// </summary>
    public Func<WorkerTest, string>? PartitionKey { get; set; }

    public Action<string>? Log { get; set; }

    public async Task<SweepResults> Run(CancellationToken ct = default)
    {
        var tests = await discover(ct);
        if (tests.Count == 0)
        {
            return new SweepResults
            {
                Findings = [], Discovered = 0, Partitions = 0, BaselineFailures = 0
            };
        }

        Log?.Invoke($"{tests.Count} test(s) discovered");

        var baseline = await runBaseline(ct);
        if (baseline.Crashed)
        {
            // No baseline means no comparison, and every "failed alone" would be unclassifiable.
            // Reporting nothing is honest; reporting findings from half the evidence is not.
            var reason = $"baseline run crashed (exit {baseline.ExitCode}): {baseline.Fault}";
            Log?.Invoke(reason);
            return new SweepResults
            {
                Findings = [],
                Discovered = tests.Count,
                Partitions = 0,
                BaselineFailures = 0,
                AbortReason = reason
            };
        }

        var passedWithSuite = new HashSet<string>(
            baseline.Outcomes.Where(o => o.Succeeded).Select(o => o.Uid), StringComparer.Ordinal);

        var baselineFailures = baseline.Outcomes.Count(o => !o.Succeeded);
        Log?.Invoke($"baseline: {baseline.Outcomes.Count} outcome(s), {baselineFailures} failed");

        var groups = group(tests);
        Log?.Invoke($"sweeping {groups.Count} {(Granularity == SweepGranularity.PerClass ? "class" : "test")}(es) " +
                    $"in their own process, {MaxParallelWorkers} at a time");

        var alone = await sweep(groups, ct);

        var findings = await classify(tests, passedWithSuite, alone, ct);

        return new SweepResults
        {
            Findings = findings,
            Discovered = tests.Count,
            Partitions = groups.Count,
            BaselineFailures = baselineFailures
        };
    }

    private async Task<IReadOnlyList<WorkerTest>> discover(CancellationToken ct)
    {
        await using var worker = await _factory.Launch(WorkerLaunchContext.Discovery, ct);
        return await worker.Discover(ct);
    }

    private async Task<WorkerRunResult> runBaseline(CancellationToken ct)
    {
        // The whole suite in one process, which is what "passed with the suite" has to mean.
        await using var worker = await _factory.Launch(new WorkerLaunchContext(0, WorkerPurpose.Lane), ct);
        return await worker.Run(null, ct);
    }

    private List<SweepGroup> group(IReadOnlyList<WorkerTest> tests)
    {
        if (Granularity == SweepGranularity.PerTest)
        {
            return [.. tests.Select(t => new SweepGroup(t.DisplayName, [t.Uid]))];
        }

        var key = PartitionKey ?? WorkPlan.ClassOf;
        return [.. tests
            .GroupBy(key, StringComparer.Ordinal)
            .Select(g => new SweepGroup(g.Key, [.. g.Select(t => t.Uid)]))];
    }

    /// <summary>Runs each group in its own fresh process, at most <c>MaxParallelWorkers</c> at once.</summary>
    private async Task<Dictionary<string, WorkerOutcome>> sweep(
        IReadOnlyList<SweepGroup> groups, CancellationToken ct)
    {
        var lanes = Math.Max(1, MaxParallelWorkers);
        using var slots = new SemaphoreSlim(lanes);

        // The slot a group gets is what picks its per-worker resources, so at most `lanes`
        // environments are ever live and a caller provisions exactly that many.
        var free = new ConcurrentBag<int>(Enumerable.Range(0, lanes));

        var results = new ConcurrentDictionary<string, WorkerOutcome>(StringComparer.Ordinal);
        var completed = 0;

        await Task.WhenAll(groups.Select(async g =>
        {
            await slots.WaitAsync(ct);
            free.TryTake(out var lane);
            try
            {
                foreach (var outcome in await runAlone(g, lane, ct))
                {
                    results[outcome.Uid] = outcome;
                }
            }
            finally
            {
                free.Add(lane);
                slots.Release();
                var done = Interlocked.Increment(ref completed);
                if (done % 50 == 0) Log?.Invoke($"  {done}/{groups.Count}");
            }
        }));

        return new Dictionary<string, WorkerOutcome>(results, StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<WorkerOutcome>> runAlone(SweepGroup group, int lane, CancellationToken ct)
    {
        try
        {
            await using var worker = await _factory.Launch(
                new WorkerLaunchContext(lane, WorkerPurpose.Isolated), ct);

            var run = await worker.Run(group.Uids, ct);

            if (run.Crashed)
            {
                // A crash is absence of evidence, not a failure — the same rule the supervisor
                // applies. Synthesise one per uid so the group is not silently missing.
                return [.. group.Uids.Select(uid => new WorkerOutcome(uid, uid, WorkerTestState.Failed)
                {
                    ErrorMessage = $"worker crashed (exit {run.ExitCode}): {run.Fault}"
                })];
            }

            return run.Outcomes;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [.. group.Uids.Select(uid => new WorkerOutcome(uid, uid, WorkerTestState.Failed)
            {
                ErrorMessage = $"could not run in isolation: {e.Message}"
            })];
        }
    }

    private async Task<List<SweepFinding>> classify(
        IReadOnlyList<WorkerTest> tests,
        HashSet<string> passedWithSuite,
        Dictionary<string, WorkerOutcome> alone,
        CancellationToken ct)
    {
        var byUid = tests.ToDictionary(t => t.Uid, StringComparer.Ordinal);
        var key = PartitionKey ?? WorkPlan.ClassOf;
        var findings = new List<SweepFinding>();

        foreach (var test in tests)
        {
            var withSuite = passedWithSuite.Contains(test.Uid);

            // A test the sweep never reported on is not evidence of anything. Say so rather than
            // reading the silence as a pass.
            if (!alone.TryGetValue(test.Uid, out var solo))
            {
                if (withSuite) continue;
                findings.Add(finding(test, SweepVerdict.FailedInBoth, "no outcome reported when run alone"));
                continue;
            }

            var soloPassed = solo.Succeeded;

            if (withSuite && soloPassed) continue;               // behaved both ways
            if (!withSuite && soloPassed)
            {
                findings.Add(finding(test, SweepVerdict.InterferenceVictim, null));
                continue;
            }

            if (!withSuite)
            {
                findings.Add(finding(test, SweepVerdict.FailedInBoth, solo.ErrorMessage));
                continue;
            }

            // Passed with the suite, failed alone — a suspect, and the only class that earns a
            // second run. Confirmed serially on lane 0 so neither a per-worker environment nor
            // contention with other sweep processes can be what failed it.
            var confirmation = await runAlone(new SweepGroup(test.DisplayName, [test.Uid]), 0, ct);
            var confirmed = confirmation.FirstOrDefault(o => o.Uid == test.Uid);

            findings.Add(confirmed is null || confirmed.Succeeded
                ? finding(test, SweepVerdict.EnvironmentSensitive, solo.ErrorMessage)
                : finding(test, SweepVerdict.OrderDependent, confirmed.ErrorMessage ?? solo.ErrorMessage));
        }

        return findings;

        SweepFinding finding(WorkerTest test, SweepVerdict verdict, string? error) =>
            new(test.Uid, test.DisplayName, verdict)
            {
                Partition = Granularity == SweepGranularity.PerTest ? test.DisplayName : key(test),
                ErrorMessage = error
            };
    }

    private sealed record SweepGroup(string Name, IReadOnlyList<string> Uids);
}
