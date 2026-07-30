namespace Bobcat.Supervisor;

/// <summary>One worker's share of a run.</summary>
public sealed record WorkLane(int Index, IReadOnlyList<string> Uids, TimeSpan Estimate)
{
    /// <summary>Partition keys assigned to this lane, in assignment order. For reporting.</summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];
}

/// <summary>
/// Splits a run across parallel workers, deciding <em>what may be separated</em> before deciding
/// <em>how to balance it</em>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Partitioning is by test class, not by test, and that is a correctness rule rather than
/// a tuning choice.</strong> Measured against Wolverine's <c>PersistenceTests</c>: splitting the
/// same 78 tests across 4 workers per <em>test</em> failed 1–4 tests non-deterministically, while
/// splitting per <em>class</em> passed 78/78 at the same wall clock.
/// </para>
/// <para>
/// The mechanism is that a test class is the unit an author reasons about. Every framework's
/// isolation contract is per class or per collection — fixtures, <c>IAsyncLifetime</c>, and any
/// static state inside the class. One real example: a class whose setup read
/// <c>var schemaName = "sqlserver" + ++count;</c> from a <c>static int</c>. Split across four
/// processes, each restarts at zero and all four collide on the same schema. Nothing about that is
/// visible in the test list — so the planner must never separate tests the author kept together.
/// </para>
/// <para>
/// Balancing is then <strong>longest-processing-time-first</strong> over whole partitions, which
/// is the standard greedy makespan heuristic. Wall clock is the slowest lane, so the largest
/// partition sets the floor: no number of workers beats the single slowest class.
/// </para>
/// </remarks>
public static class WorkPlan
{
    /// <summary>
    /// Used for a test with no recorded duration when nothing else is known. Only the ratios
    /// between partitions matter, so the absolute value is arbitrary.
    /// </summary>
    private static readonly TimeSpan UnknownTestEstimate = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The default partition key: the test's declaring class.
    /// </summary>
    /// <remarks>
    /// Derived from the display name because it is the only structural naming signal every
    /// front-end supplies. Two shapes are recognised — Bobcat's <c>Feature/Scenario</c> and the
    /// dotted <c>Namespace.Class.method</c> of xUnit, tUnit and friends. Theory arguments are
    /// stripped first, since <c>method(value: "a.b")</c> would otherwise split one method into
    /// partitions per argument.
    /// </remarks>
    public static string ClassOf(WorkerTest test)
    {
        var name = test.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) return test.Uid;

        // Bobcat uids and display names are "Feature/Scenario"; the feature is the grouping.
        var slash = name.LastIndexOf('/');
        if (slash > 0) return name[..slash];

        // Strip theory/parameterised arguments before looking for the method separator.
        var openParen = name.IndexOf('(');
        var withoutArguments = openParen > 0 ? name[..openParen] : name;

        var dot = withoutArguments.LastIndexOf('.');
        return dot > 0 ? withoutArguments[..dot] : withoutArguments;
    }

    /// <summary>
    /// Plans <paramref name="laneCount"/> lanes over <paramref name="tests"/>.
    /// </summary>
    /// <param name="knownDurations">
    /// Previously observed per-test durations, keyed by uid. Optional — a first run has none, and
    /// then partitions are weighted by test count instead. Partial data is fine: tests with no
    /// entry are charged the median of the ones that have, so adding a test does not skew a plan.
    /// </param>
    /// <returns>
    /// Lanes in descending estimate order, never more than there are partitions, and never empty.
    /// The plan is deterministic: the same inputs always produce the same lanes.
    /// </returns>
    public static IReadOnlyList<WorkLane> Build(
        IReadOnlyList<WorkerTest> tests,
        int laneCount,
        Func<WorkerTest, string>? partitionKey = null,
        IReadOnlyDictionary<string, TimeSpan>? knownDurations = null)
    {
        if (tests.Count == 0) return [];

        // One lane means no partitioning decision to make. Returning discovery order verbatim
        // keeps the single-worker path byte-for-byte what it was before parallelism existed.
        if (laneCount <= 1)
        {
            return [new WorkLane(0, tests.Select(t => t.Uid).ToList(), Total(tests, knownDurations))];
        }

        var key = partitionKey ?? ClassOf;
        var fallback = MedianOf(tests, knownDurations);

        var partitions = tests
            .GroupBy(key, StringComparer.Ordinal)
            .Select(group => (
                Key: group.Key,
                Uids: group.Select(t => t.Uid).ToList(),
                Estimate: group.Aggregate(TimeSpan.Zero, (sum, t) => sum + Estimate(t, knownDurations, fallback))))
            // Longest first, then by key so ties never reorder between runs.
            .OrderByDescending(p => p.Estimate)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

        var lanes = Enumerable.Range(0, Math.Min(laneCount, partitions.Count))
            .Select(index => (Index: index, Load: TimeSpan.Zero, Uids: new List<string>(), Keys: new List<string>()))
            .ToList();

        foreach (var partition in partitions)
        {
            // Greedy: whichever lane is currently lightest takes the next-largest partition.
            var lightest = 0;
            for (var i = 1; i < lanes.Count; i++)
            {
                if (lanes[i].Load < lanes[lightest].Load) lightest = i;
            }

            var lane = lanes[lightest];
            lane.Uids.AddRange(partition.Uids);
            lane.Keys.Add(partition.Key);
            lanes[lightest] = lane with { Load = lane.Load + partition.Estimate };
        }

        return lanes
            .Select(lane => new WorkLane(lane.Index, lane.Uids, lane.Load) { Partitions = lane.Keys })
            .OrderByDescending(lane => lane.Estimate)
            .ToList();
    }

    private static TimeSpan Estimate(
        WorkerTest test, IReadOnlyDictionary<string, TimeSpan>? known, TimeSpan fallback)
        => known is not null && known.TryGetValue(test.Uid, out var duration) ? duration : fallback;

    private static TimeSpan Total(IReadOnlyList<WorkerTest> tests, IReadOnlyDictionary<string, TimeSpan>? known)
    {
        var fallback = MedianOf(tests, known);
        return tests.Aggregate(TimeSpan.Zero, (sum, t) => sum + Estimate(t, known, fallback));
    }

    /// <summary>
    /// What to charge a test we have never timed. The median of what we do know beats a constant:
    /// a suite of 30-second integration tests should not have its one new test costed at a second.
    /// </summary>
    private static TimeSpan MedianOf(
        IReadOnlyList<WorkerTest> tests, IReadOnlyDictionary<string, TimeSpan>? known)
    {
        if (known is null || known.Count == 0) return UnknownTestEstimate;

        var observed = tests
            .Where(t => known.ContainsKey(t.Uid))
            .Select(t => known[t.Uid])
            .OrderBy(d => d)
            .ToList();

        if (observed.Count == 0) return UnknownTestEstimate;

        return observed[observed.Count / 2];
    }
}
