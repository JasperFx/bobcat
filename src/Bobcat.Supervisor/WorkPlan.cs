using Bobcat.Ledger;

namespace Bobcat.Supervisor;

/// <summary>
/// How many of the planned tests the supplied known durations matched (bobcat#663) — the
/// difference between a plan balanced by duration and one that quietly fell back to test count.
/// </summary>
/// <remarks>
/// xUnit v3's uid hashes the assembly path, so durations keyed by uid can match nothing on another
/// machine, configuration or TFM: Marten's committed ledger matched 0 of 2525 tests on CI while the
/// run read exactly like a balanced one. The ledger keys by display name since issue #730, and a
/// duration is looked up by that key first and by uid second.
/// </remarks>
public sealed record DurationCoverage(int Matched, int Tests)
{
    /// <summary>True when no supplied duration named any planned test, so lanes were balanced by count.</summary>
    public bool BalancedByCount => Matched == 0;

    public override string ToString()
        => BalancedByCount
            ? $"Known durations matched 0/{Tests} test(s): lanes balanced by test count"
            : $"Known durations matched {Matched}/{Tests} test(s)";
}

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
    private static readonly TimeSpan unknownTestEstimate = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The default partition key: the test's declaring class.
    /// </summary>
    /// <remarks>
    /// Derived from the display name because it is the only structural naming signal every
    /// front-end supplies. Three shapes are recognised — <c>Feature/Scenario</c>, Bobcat's own
    /// <c>Feature: Scenario</c> display name, and the dotted <c>Namespace.Class.method</c> of
    /// xUnit, tUnit and friends.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// The <c>Feature: Scenario</c> shape is not decoration. <c>SpecNodeMapping</c> builds uids as
    /// <c>Feature/Scenario</c> but display names as <c>Feature: Scenario</c>, and this method reads
    /// the display name — so recognising only the slash meant every Bobcat scenario fell through to
    /// "no separator found" and became its own partition. Class-level partitioning silently
    /// degraded to per-test for Bobcat's own workers, which is the one thing the partitioner exists
    /// to prevent. Found by an end-to-end isolation sweep, which expected two features and got
    /// seven scenarios.
    /// </para>
    /// <para>
    /// Arguments are stripped <em>before</em> any separator is looked for, not after. A theory
    /// argument can contain any of these separators — <c>method(path: "a/b")</c> — and splitting on
    /// one inside the parentheses would shatter a method into a partition per argument, which is
    /// the same defect from the other direction.
    /// </para>
    /// </remarks>
    public static string ClassOf(WorkerTest test)
    {
        var name = test.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) return test.Uid;

        var openParen = name.IndexOf('(');
        var withoutArguments = openParen > 0 ? name[..openParen] : name;

        // "Feature/Scenario" — the uid shape, and what a front-end using paths will produce.
        var slash = withoutArguments.LastIndexOf('/');
        if (slash > 0) return withoutArguments[..slash];

        // "Feature: Scenario" — Bobcat's display name. First colon, because the feature is the
        // prefix and a scenario title may well contain another one.
        var colon = withoutArguments.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0) return withoutArguments[..colon];

        var dot = withoutArguments.LastIndexOf('.');
        return dot > 0 ? withoutArguments[..dot] : withoutArguments;
    }

    /// <summary>
    /// Plans <paramref name="laneCount"/> lanes over <paramref name="tests"/>.
    /// </summary>
    /// <param name="knownDurations">
    /// Previously observed per-test durations, keyed as the ledger keys them — display name, or
    /// uid where two tests share a display name (<see cref="LedgerKeys"/>) — or by uid. Optional — a first run has none, and
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
        var durations = durationsOf(tests, knownDurations);

        if (laneCount <= 1)
        {
            return [new WorkLane(0, tests.Select(t => t.Uid).ToList(), total(tests, durations))];
        }

        var key = partitionKey ?? ClassOf;
        var fallback = medianOf(durations);

        var partitions = tests
            .GroupBy(key, StringComparer.Ordinal)
            .Select(group => (
                Key: group.Key,
                Uids: group.Select(t => t.Uid).ToList(),
                Estimate: group.Aggregate(TimeSpan.Zero, (sum, t) => sum + estimate(t, durations, fallback))))
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

    /// <summary>
    /// How many of <paramref name="tests"/> have an entry in <paramref name="knownDurations"/>
    /// (bobcat#663). Null when no durations were supplied at all, which is a deliberate count
    /// balance rather than a silent fallback.
    /// </summary>
    public static DurationCoverage? CoverageOf(
        IReadOnlyList<WorkerTest> tests, IReadOnlyDictionary<string, TimeSpan>? knownDurations)
        => knownDurations is null
            ? null
            : new DurationCoverage(durationsOf(tests, knownDurations).Count, tests.Count);

    /// <summary>
    /// Each test's known duration, by uid — looked up by its ledger key (display name, or uid
    /// where two of <paramref name="tests"/> share one), then by uid, so a ledger from another
    /// machine and a hand-built uid dictionary both join. Tests with neither are absent.
    /// </summary>
    private static Dictionary<string, TimeSpan> durationsOf(
        IReadOnlyList<WorkerTest> tests, IReadOnlyDictionary<string, TimeSpan>? known)
        => KnownDurationsByUid(tests.Select(t => (t.Uid, t.DisplayName)), known);

    /// <summary>
    /// The one join between a run's tests and a set of known durations (bobcat#748), shared by
    /// the planner and <see cref="RunTiming"/> so the two can never disagree about which tests
    /// have a history: each test's ledger key first, then its uid. Keyed by uid.
    /// </summary>
    internal static Dictionary<string, TimeSpan> KnownDurationsByUid(
        IEnumerable<(string Uid, string DisplayName)> tests, IReadOnlyDictionary<string, TimeSpan>? known)
    {
        var durations = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        if (known is null || known.Count == 0) return durations;

        var list = tests.ToList();
        var keys = LedgerKeys.For(list);
        foreach (var (uid, _) in list)
        {
            if (known.TryGetValue(keys[uid], out var duration) || known.TryGetValue(uid, out duration))
            {
                durations[uid] = duration;
            }
        }

        return durations;
    }

    private static TimeSpan estimate(
        WorkerTest test, IReadOnlyDictionary<string, TimeSpan> durations, TimeSpan fallback)
        => durations.TryGetValue(test.Uid, out var duration) ? duration : fallback;

    private static TimeSpan total(IReadOnlyList<WorkerTest> tests, IReadOnlyDictionary<string, TimeSpan> durations)
    {
        var fallback = medianOf(durations);
        return tests.Aggregate(TimeSpan.Zero, (sum, t) => sum + estimate(t, durations, fallback));
    }

    /// <summary>
    /// What to charge a test we have never timed. The median of what we do know beats a constant:
    /// a suite of 30-second integration tests should not have its one new test costed at a second.
    /// </summary>
    private static TimeSpan medianOf(IReadOnlyDictionary<string, TimeSpan> durations)
    {
        if (durations.Count == 0) return unknownTestEstimate;

        var observed = durations.Values.OrderBy(d => d).ToList();
        return observed[observed.Count / 2];
    }
}
