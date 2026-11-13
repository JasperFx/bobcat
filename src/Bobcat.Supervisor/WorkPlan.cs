using System.Globalization;
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

/// <summary>
/// The order a supervised run's class partitions run in (bobcat#578). <see cref="Declared"/> is the
/// plan as <see cref="WorkPlan.Build"/> has always made it: longest partition first, lanes balanced,
/// and one request per lane that leaves the order inside it to the test framework.
/// <see cref="Random"/> shuffles the partitions with a seed and deals them round-robin over the lanes,
/// and each lane runs its partitions one request at a time in that order — so which classes share
/// a process, and what runs before what inside it, change with the seed. That is how coupling between
/// classes is found (a <c>static int</c> schema counter, a table one class leaves behind for the
/// next). Tests inside a class are never reordered.
/// </summary>
public enum RunOrder
{
    Declared,
    Random
}

/// <summary>
/// How a supervised run hands its class partitions to its lanes (bobcat#577).
/// <see cref="Eager"/>, the default, is the plan as <see cref="WorkPlan.Build"/> makes it: every lane
/// is dealt its partitions before anything runs, balanced longest-first on the known durations, and
/// given them as one request. <see cref="Lazy"/> plans nothing ahead: the partitions wait in one queue
/// (<see cref="WorkPlan.Queue"/>, longest first) and each lane pulls the next as soon as it is idle,
/// one request per partition into its long-lived worker. A lane that drew short classes simply takes
/// more of them, so a run with no ledger — or a ledger that is wrong about this machine — still
/// finishes its lanes close together. Classes are never split either way.
/// </summary>
/// <remarks>
/// The price, stated rather than hidden: under <see cref="Lazy"/> which classes share a process is
/// decided by timing, so it can change between two runs of the same suite. That surfaces coupling
/// between classes the way <see cref="RunOrder.Random"/> does, but less reproducibly — a coupling found
/// under lazy is reproduced with <c>--order random --seed N</c>, not by running lazy again.
/// </remarks>
public enum Distribution
{
    Eager,
    Lazy
}

/// <summary>
/// One class partition waiting in a lazy run's queue (bobcat#577): its key, its tests in discovery
/// order, and what the planner expects it to cost.
/// </summary>
public sealed record WorkPartition(string Key, IReadOnlyList<string> Uids, TimeSpan Estimate);

/// <summary>
/// One leg of a run split across machines (bobcat#576): <c>bobcat run --shard 2/4</c> is
/// <c>new RunShard(2, 4)</c>. <see cref="Index"/> is 1-based, as the flag is written.
/// </summary>
public sealed record RunShard(int Index, int Count)
{
    /// <summary>
    /// Reads <c>i/n</c>. A refusal names what is wrong, in the words the tool prints: malformed, a
    /// count below 1, an index below 1 or above the count.
    /// </summary>
    public static RunShard? Parse(string text, out string? refusal)
    {
        refusal = null;
        var parts = text.Trim().Split('/');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            refusal = $"--shard '{text}' is not a shard; write it as i/n, the leg and the number of legs, such as 2/4.";
            return null;
        }

        if (count < 1)
        {
            refusal = $"--shard '{text}': the number of legs must be 1 or more.";
            return null;
        }

        if (index < 1 || index > count)
        {
            refusal = $"--shard '{text}': the leg must be between 1 and {count}; legs are numbered from 1.";
            return null;
        }

        return new RunShard(index, count);
    }

    /// <summary><c>i/n</c>, the flag's own spelling.</summary>
    public override string ToString() => $"{Index}/{Count}";
}

/// <summary>
/// What one shard of a run holds (bobcat#576), and what it was weighed against: its tests in
/// discovery order, the partitions (classes) it took, and the whole run's figures beside its own.
/// </summary>
public sealed record ShardLeg(
    RunShard Shard,
    IReadOnlyList<WorkerTest> Tests,
    IReadOnlyList<string> Partitions,
    int TotalPartitions,
    int TotalTests,
    TimeSpan Estimate,
    TimeSpan TotalEstimate,
    DurationCoverage? Coverage)
{
    /// <summary>True when known durations weighed the legs; false when they were balanced by test count.</summary>
    public bool BalancedByDuration => Coverage is { BalancedByCount: false };

    /// <summary>
    /// The sentence a shard is announced with in the supervisor's log and its report:
    /// <c>Shard 2/4: 17 of 68 class(es) (230 of 912 test(s)), estimated 3m10s of 12m40s (Known durations matched 410/456 test(s))</c>.
    /// Without known durations no time is claimed, because the estimate would be a test count in disguise.
    /// </summary>
    public override string ToString()
    {
        var head = $"Shard {Shard}: {Partitions.Count} of {TotalPartitions} class(es) ({Tests.Count} of {TotalTests} test(s))";
        if (BalancedByDuration) return $"{head}, estimated {format(Estimate)} of {format(TotalEstimate)} ({Coverage})";

        return Coverage is null
            ? $"{head}, legs balanced by test count (no known durations)"
            : $"{head}, legs balanced by test count (known durations matched 0/{Coverage.Tests} test(s))";
    }

    private static string format(TimeSpan span)
        => span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:D2}s"
            : $"{span.TotalSeconds:0.#}s";
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
    /// <param name="order">
    /// <see cref="RunOrder.Declared"/> (the default) balances the lanes longest-first;
    /// <see cref="RunOrder.Random"/> shuffles the partitions with <paramref name="seed"/> and deals
    /// them round-robin, into one lane when <paramref name="laneCount"/> is one (bobcat#578).
    /// </param>
    /// <param name="seed">The seed a random order is reproduced from; ignored for a declared one.</param>
    /// <returns>
    /// Lanes in descending estimate order (a random plan: in lane order), never more than there are
    /// partitions, and never empty. The plan is deterministic: the same inputs always produce the
    /// same lanes, seed included.
    /// </returns>
    public static IReadOnlyList<WorkLane> Build(
        IReadOnlyList<WorkerTest> tests,
        int laneCount,
        Func<WorkerTest, string>? partitionKey = null,
        IReadOnlyDictionary<string, TimeSpan>? knownDurations = null,
        RunOrder order = RunOrder.Declared,
        int seed = 0)
    {
        if (tests.Count == 0) return [];

        // One lane means no partitioning decision to make. Returning discovery order verbatim
        // keeps the single-worker path byte-for-byte what it was before parallelism existed.
        var durations = durationsOf(tests, knownDurations);

        if (order == RunOrder.Random) return shuffled(tests, laneCount, partitionKey ?? ClassOf, durations, seed);

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
    /// The queue a lazy run's lanes pull from (bobcat#577): every class partition of
    /// <paramref name="tests"/>, weighed exactly as <see cref="Build"/> weighs them, longest first
    /// and then by key — or, under <see cref="RunOrder.Random"/>, sorted by key and shuffled with
    /// <paramref name="seed"/>, the same order a one-lane random plan runs them in.
    /// </summary>
    /// <remarks>
    /// Longest first because the last partition pulled sets when the run ends: a long class pulled
    /// late leaves every other lane idle while it runs. A pure function of its inputs, like
    /// <see cref="Build"/>; what is not deterministic under lazy is which lane takes which partition.
    /// </remarks>
    public static IReadOnlyList<WorkPartition> Queue(
        IReadOnlyList<WorkerTest> tests,
        Func<WorkerTest, string>? partitionKey = null,
        IReadOnlyDictionary<string, TimeSpan>? knownDurations = null,
        RunOrder order = RunOrder.Declared,
        int seed = 0)
    {
        if (tests.Count == 0) return [];

        var key = partitionKey ?? ClassOf;
        var durations = durationsOf(tests, knownDurations);
        var fallback = medianOf(durations);

        var partitions = tests
            .GroupBy(key, StringComparer.Ordinal)
            .Select(group => new WorkPartition(
                group.Key,
                group.Select(t => t.Uid).ToList(),
                group.Aggregate(TimeSpan.Zero, (sum, t) => sum + estimate(t, durations, fallback))))
            .ToList();

        return order == RunOrder.Random
            ? Shuffle(partitions.OrderBy(p => p.Key, StringComparer.Ordinal).ToList(), seed)
            : partitions
                .OrderByDescending(p => p.Estimate)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>
    /// The tests of one leg of a run split across machines (bobcat#576): <see cref="Legs"/>, then
    /// leg <paramref name="shard"/>.
    /// </summary>
    public static ShardLeg Shard(
        IReadOnlyList<WorkerTest> tests,
        RunShard shard,
        Func<WorkerTest, string>? partitionKey = null,
        IReadOnlyDictionary<string, TimeSpan>? knownDurations = null)
    {
        if (shard.Count < 1 || shard.Index < 1 || shard.Index > shard.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(shard), shard, "A shard is i/n with 1 <= i <= n.");
        }

        return Legs(tests, shard.Count, partitionKey, knownDurations)[shard.Index - 1];
    }

    /// <summary>
    /// Every leg of a run split <paramref name="count"/> ways (bobcat#576): the same class partitions
    /// <see cref="Build"/> plans lanes from, balanced longest-first over legs instead of lanes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A pure function of the test list and the known durations.</b> Each leg runs on another
    /// machine and computes only its own share, so the legs can only be disjoint and cover everything
    /// if every machine computes the same split: partitions are weighed as <see cref="Build"/> weighs
    /// them (known duration, the median for a test with none, one nominal second each when nothing is
    /// known — so a count balance), sorted heaviest first and then by key ordinally, and dealt to the
    /// lightest leg, the lowest-numbered on a tie. Discovery order never enters it. Two machines
    /// reading <em>different</em> ledgers can disagree, and then legs overlap or drop a class — so
    /// every leg must read the same ledger artifact.
    /// </para>
    /// <para>
    /// A leg's tests keep their discovery order. A leg beyond the number of partitions is empty: a leg
    /// with nothing to run, not an error.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ShardLeg> Legs(
        IReadOnlyList<WorkerTest> tests,
        int count,
        Func<WorkerTest, string>? partitionKey = null,
        IReadOnlyDictionary<string, TimeSpan>? knownDurations = null)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "A run splits into 1 or more legs.");

        var key = partitionKey ?? ClassOf;
        var durations = durationsOf(tests, knownDurations);
        var fallback = medianOf(durations);
        var coverage = CoverageOf(tests, knownDurations);

        var partitions = tests
            .GroupBy(key, StringComparer.Ordinal)
            .Select(group => (
                Key: group.Key,
                Uids: group.Select(t => t.Uid).ToList(),
                Estimate: group.Aggregate(TimeSpan.Zero, (sum, t) => sum + estimate(t, durations, fallback))))
            .OrderByDescending(p => p.Estimate)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

        var loads = new TimeSpan[count];
        var members = Enumerable.Range(0, count).Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray();
        var keys = Enumerable.Range(0, count).Select(_ => new List<string>()).ToArray();

        foreach (var partition in partitions)
        {
            var lightest = 0;
            for (var i = 1; i < count; i++)
            {
                if (loads[i] < loads[lightest]) lightest = i;
            }

            members[lightest].UnionWith(partition.Uids);
            keys[lightest].Add(partition.Key);
            loads[lightest] += partition.Estimate;
        }

        var totalEstimate = partitions.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Estimate);

        return Enumerable.Range(0, count)
            .Select(i => new ShardLeg(
                new RunShard(i + 1, count),
                tests.Where(t => members[i].Contains(t.Uid)).ToList(),
                keys[i].Order(StringComparer.Ordinal).ToList(),
                partitions.Count,
                tests.Count,
                loads[i],
                totalEstimate,
                coverage))
            .ToList();
    }

    /// <summary>
    /// The random plan (bobcat#578): partitions sorted by key, shuffled with <paramref name="seed"/>,
    /// dealt round-robin over the lanes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sorted before it is shuffled</b>, so the order depends on nothing but the set of classes
    /// and the seed. Discovery order differs between frameworks and machines, and a seed that
    /// reproduced an order only on the machine that minted it would be no seed at all.
    /// </para>
    /// <para>
    /// <b>Round-robin, not balanced.</b> Balancing would put the shuffled partitions back into
    /// nearly the same lanes every run, the heavy classes always apart; a random run is for finding
    /// coupling, not for speed, and is expected to be slower than a balanced one. Tests inside a
    /// partition keep their discovery order: a class is the unit the author kept together.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<WorkLane> shuffled(
        IReadOnlyList<WorkerTest> tests, int laneCount, Func<WorkerTest, string> key,
        IReadOnlyDictionary<string, TimeSpan> durations, int seed)
    {
        var fallback = medianOf(durations);

        var partitions = tests
            .GroupBy(key, StringComparer.Ordinal)
            .Select(group => (
                Key: group.Key,
                Uids: group.Select(t => t.Uid).ToList(),
                Estimate: group.Aggregate(TimeSpan.Zero, (sum, t) => sum + estimate(t, durations, fallback))))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();

        var dealt = Shuffle(partitions, seed);

        var lanes = Enumerable.Range(0, Math.Max(1, Math.Min(laneCount, dealt.Count)))
            .Select(index => (Index: index, Load: TimeSpan.Zero, Uids: new List<string>(), Keys: new List<string>()))
            .ToList();

        for (var i = 0; i < dealt.Count; i++)
        {
            var lane = lanes[i % lanes.Count];
            lane.Uids.AddRange(dealt[i].Uids);
            lane.Keys.Add(dealt[i].Key);
            lanes[i % lanes.Count] = lane with { Load = lane.Load + dealt[i].Estimate };
        }

        return lanes
            .Select(lane => new WorkLane(lane.Index, lane.Uids, lane.Load) { Partitions = lane.Keys })
            .ToList();
    }

    /// <summary>
    /// <paramref name="items"/> in the order <paramref name="seed"/> gives them: a Fisher–Yates
    /// shuffle over a generator Bobcat owns (<see cref="SeededRandom"/>), so one seed gives one order
    /// on every machine and every .NET version. <c>System.Random</c>'s sequence for a seed is not
    /// promised stable across runtime versions, and a seed printed on a CI image has to reproduce
    /// the run on a developer's machine.
    /// </summary>
    public static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> items, int seed)
    {
        var result = items.ToArray();
        var random = new SeededRandom(seed);

        for (var i = result.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        return result;
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

/// <summary>
/// A small, fixed pseudo-random generator (SplitMix64) whose sequence for a seed is part of Bobcat's
/// contract: <c>bobcat run --order random --seed N</c> must give the same order everywhere, and
/// <c>WorkPlanTests</c> pins the sequence with literal values. Never reach for <c>System.Random</c>
/// here — its algorithm has changed between .NET versions.
/// </summary>
internal struct SeededRandom
{
    private ulong _state;

    public SeededRandom(int seed)
    {
        // Spread the 32-bit seed over the state so neighbouring seeds give unrelated orders.
        _state = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL);
    }

    public ulong NextUInt64()
    {
        unchecked
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A value in <c>[0, bound)</c>. The modulo bias is immaterial for shuffling a test list.</summary>
    public int Next(int bound) => (int)(NextUInt64() % (ulong)bound);
}
