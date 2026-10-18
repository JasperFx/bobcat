namespace Bobcat.Supervisor;

/// <summary>What one test cost the run, summed across every attempt it took.</summary>
public sealed record TestTiming(string Uid, string DisplayName)
{
    /// <summary>Every attempt this test took, added up.</summary>
    public required TimeSpan Total { get; init; }

    /// <summary>What the first attempt alone cost — what the test would cost if it never flaked.</summary>
    public required TimeSpan FirstAttempt { get; init; }

    public required int Attempts { get; init; }

    /// <summary>
    /// What the retries cost on top of running the test once. Per-process profiling never sees
    /// this: each attempt looks like an ordinary run of an ordinary test, and only something
    /// holding the attempt history knows a 4s test was actually 12s of the run.
    /// </summary>
    public TimeSpan RetryCost => Total - FirstAttempt;
}

/// <summary>
/// How fast this runner was against the ledger's history of the same tests (bobcat#705).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Index"/> is this run's summed first-attempt duration over every test the known
/// durations name, divided by the known (median) durations of those same tests. 1.0 is a runner
/// as fast as the history; 1.3 is one that ran the same tests 30% slower.
/// </para>
/// <para>
/// The first attempt, not the total: a test that flaked in this run cost more because it ran
/// twice, not because the runner was slow, and folding that in would blame the machine for the
/// suite. The comparison is over the same tests on both sides, so a plan that moved tests between
/// lanes cannot change it — which is what lets it separate "the runner was slower that day" from
/// "the plan was worse" (Polecat, #565 lesson 9).
/// </para>
/// </remarks>
public sealed record RunnerSpeed(int Matched, TimeSpan ThisRun, TimeSpan Known)
{
    /// <summary><c>this run / known</c> over the matched tests. Above 1 is slower than the history.</summary>
    public double Index => ThisRun / Known;
}

/// <summary>One first-pass lane: what <see cref="WorkPlan"/> expected it to cost, and what it took (bobcat#705).</summary>
/// <param name="Predicted">
/// The planner's estimate, in summed test time. Null when the plan was balanced by test count:
/// the planner then charges each test a nominal second, which is a ratio, not a duration.
/// </param>
/// <param name="TestTime">The lane's tests' first-attempt durations added up — the same units as <paramref name="Predicted"/>.</param>
/// <param name="Elapsed">How long the lane's worker took to run its share — the lane's own wall clock. Null when not timed.</param>
public sealed record LaneTiming(int Index, int Tests, TimeSpan? Predicted, TimeSpan TestTime, TimeSpan? Elapsed);

/// <summary>
/// Where a supervised run spent its time — issue #56 layer 1, "describe this run".
/// </summary>
/// <remarks>
/// <para>
/// Pure computation over <see cref="SupervisorResults"/>, so it is testable without a run and
/// renderable more than one way. <see cref="RunReport"/> owns the rendering.
/// </para>
/// <para>
/// The questions here are properties of the <em>run</em>, not of a process, which is why a
/// profiler does not answer them: which tests are on the critical path, what the retries and the
/// isolation actually cost, and how much of the fleet was doing anything. The raw material —
/// <see cref="WorkerOutcome.Duration"/> per attempt, <see cref="SupervisorAttempt.Placement"/> —
/// was already being collected and simply never added up.
/// </para>
/// <para>
/// <strong>Report, don't act.</strong> Nothing here fails a build or skips a test. A duration
/// threshold turned into a build failure converts a useful signal into a flaky one; whether a
/// slow test is a bug or a genuinely slow integration test is a judgement, and this is the
/// evidence for making it.
/// </para>
/// </remarks>
public sealed class RunTiming
{
    private RunTiming()
    {
    }

    /// <summary>Every test that reported a duration, slowest first.</summary>
    public IReadOnlyList<TestTiming> Tests { get; private init; } = [];

    /// <summary>The supervisor's own measure of the whole run, start to finish.</summary>
    public TimeSpan WallClock { get; private init; }

    /// <summary>Every attempt's reported duration, added up.</summary>
    public TimeSpan Measured { get; private init; }

    /// <summary>Total time spent launching worker processes. See <see cref="SupervisorResults.WorkerLaunchTime"/>.</summary>
    public TimeSpan LaunchOverhead { get; private init; }

    /// <summary>
    /// Time workers spent running tests, summed across workers. Zero when nothing measured it —
    /// hand-built results, or a run older than bobcat#664.
    /// </summary>
    public TimeSpan WorkerBusy { get; private init; }

    /// <summary>How many lanes the run planned. One means every overlap happened inside one worker.</summary>
    public int Lanes { get; private init; }

    /// <summary>What every attempt after a first one cost, across the whole run.</summary>
    public TimeSpan RetryCost { get; private init; }

    /// <summary>
    /// What running tests alone cost. Isolation buys reliability by spending wall clock, and that
    /// price should be a number rather than something folded invisibly into the total.
    /// </summary>
    public TimeSpan IsolationCost { get; private init; }

    /// <summary>
    /// Tests whose framework reported no duration at all. Never zero-filled: a test that was not
    /// measured is not a test that took no time, and averaging the difference away would make
    /// every figure below quietly wrong.
    /// </summary>
    public int Unmeasured { get; private init; }

    /// <summary>
    /// This runner's speed against the known durations of the same tests (bobcat#705). Null when
    /// no durations were supplied, or none named a test this run measured.
    /// </summary>
    public RunnerSpeed? RunnerSpeed { get; private init; }

    /// <summary>
    /// The first pass's lanes, predicted against realised (bobcat#705), in the plan's order.
    /// Empty unless the run planned more than one lane: one lane has no balance to judge.
    /// </summary>
    public IReadOnlyList<LaneTiming> LanePlan { get; private init; } = [];

    /// <summary>True when the lane plan was balanced by measured durations rather than test count.</summary>
    public bool BalancedByDuration => LanePlan.Count > 0 && LanePlan[0].Predicted is not null;

    /// <summary>What the planner expected the slowest lane to cost. Null when it balanced by count.</summary>
    public TimeSpan? PredictedMakespan
        => BalancedByDuration ? LanePlan.Max(l => l.Predicted!.Value) : null;

    /// <summary>
    /// <see cref="PredictedMakespan"/> scaled by <see cref="RunnerSpeed"/> — what the plan should
    /// have cost on <em>this</em> runner. The gap between this and the realised lane test time is
    /// the plan's own error, with the runner's speed taken out.
    /// </summary>
    public TimeSpan? SpeedAdjustedMakespan
        => PredictedMakespan is { } predicted && RunnerSpeed is { } speed ? predicted * speed.Index : null;

    /// <summary>The slowest lane's test time — the realised side of <see cref="PredictedMakespan"/>, in the same units.</summary>
    public TimeSpan? RealisedMakespan => LanePlan.Count > 0 ? LanePlan.Max(l => l.TestTime) : null;

    /// <summary>The slowest lane's own wall clock. Null when a lane was not timed.</summary>
    public TimeSpan? RealisedLaneWallClock
        => LanePlan.Count > 0 && LanePlan.All(l => l.Elapsed is not null) ? LanePlan.Max(l => l.Elapsed!.Value) : null;

    /// <summary>
    /// The plan's expected imbalance: slowest lane over the mean lane, from the planner's own
    /// weights (durations, or test counts when it balanced by count). 1.0 is perfectly even.
    /// </summary>
    /// <remarks>
    /// A ratio rather than a time, so it means the same thing on a fast runner and a slow one, and
    /// for a count-balanced plan whose estimates are not durations at all.
    /// </remarks>
    public double? PredictedImbalance
        => LanePlan.Count > 1 ? imbalance(LanePlan.Select(l => (l.Predicted ?? TimeSpan.FromSeconds(l.Tests)).TotalMilliseconds)) : null;

    /// <summary>
    /// The realised imbalance: slowest lane over the mean lane, by each lane's wall clock where every
    /// lane was timed, else by its test time. Set against <see cref="PredictedImbalance"/>, this is
    /// whether balancing helped — independent of how fast the runner was.
    /// </summary>
    public double? RealisedImbalance
        => LanePlan.Count < 2
            ? null
            : imbalance(LanePlan.Select(l => (RealisedLaneWallClock is null ? l.TestTime : l.Elapsed!.Value).TotalMilliseconds));

    private static double? imbalance(IEnumerable<double> loads)
    {
        var values = loads.ToList();
        var mean = values.Average();
        return mean > 0 ? values.Max() / mean : null;
    }

    /// <summary>True when at least one test reported a duration, so there is anything to say.</summary>
    public bool IsMeasured => Tests.Count > 0;

    /// <summary>
    /// <c>sum(test durations) / wall clock</c> — how many tests' worth of work the run got done
    /// per unit of wall clock.
    /// </summary>
    /// <remarks>
    /// The single most informative ratio measured on a real suite: Wolverine's
    /// <c>PersistenceTests</c> came in at 1.07x, which said xUnit's in-process parallelism was
    /// doing almost nothing because the collection fixtures serialize it. Below 1 the harness
    /// itself — process launch, discovery, gaps between passes — is a visible share of the run.
    /// </remarks>
    public double? ParallelEfficiency => Share(Measured);

    /// <summary>
    /// <c>worker busy time / wall clock</c> — how many workers were running tests at once, on
    /// average: the supervisor's own parallelism (bobcat#664). Never above <see cref="Lanes"/>
    /// plus the odd solo process.
    /// </summary>
    public double? LaneConcurrency => WorkerBusy > TimeSpan.Zero ? Share(WorkerBusy) : null;

    /// <summary>
    /// <c>sum(test durations) / worker busy time</c> — how much test time each worker packed into
    /// its own running time (bobcat#664). Above 1 is the test framework's in-process parallelism
    /// (xUnit running collections side by side), which no number of lanes produced.
    /// </summary>
    /// <remarks>
    /// Found on Marten (JasperFx/marten#5687, lesson 7): <see cref="ParallelEfficiency"/> read
    /// 1.56x on a single sequential worker, which reads as lanes that overlapped. It was xUnit's
    /// in-worker parallelism. The product of this and <see cref="LaneConcurrency"/> is roughly
    /// <see cref="ParallelEfficiency"/>; the split is what says whether more workers would help.
    /// </remarks>
    public double? InWorkerConcurrency => WorkerBusy > TimeSpan.Zero ? Measured / WorkerBusy : null;

    /// <summary>What fraction of wall clock a span accounts for. Null when the run was not timed.</summary>
    public double? Share(TimeSpan span)
        => WallClock > TimeSpan.Zero ? span / WallClock : null;

    /// <summary>
    /// What fraction of wall clock the slowest <paramref name="count"/> tests account for.
    /// </summary>
    /// <remarks>
    /// The percentage is the part that makes someone act. A bare <c>60.9s</c> reads as "integration
    /// tests are slow"; "one test is 35% of the run" reads as something to go and look at. The real
    /// case was a <c>try_it_out</c> in a <c>Bugs/</c> folder with no assertions and a one-minute
    /// <c>Task.Delay</c> — a committed scratch repro that could only ever cost a minute of every
    /// CI run, and that no test report was ever going to point at.
    /// </remarks>
    public double? Concentration(int count)
        => Share(Tests.Take(count).Aggregate(TimeSpan.Zero, (sum, test) => sum + test.Total));

    /// <summary>The slowest tests, slowest first.</summary>
    public IReadOnlyList<TestTiming> Slowest(int count) => Tests.Take(count).ToList();

    public static RunTiming For(SupervisorResults results)
    {
        var timings = new List<TestTiming>();
        var unmeasured = 0;

        foreach (var test in results.Tests)
        {
            var durations = test.Attempts.Select(a => a.Outcome.Duration).ToList();
            if (durations.All(d => d is null))
            {
                unmeasured++;
                continue;
            }

            timings.Add(new TestTiming(test.Uid, test.DisplayName)
            {
                Total = durations.Aggregate(TimeSpan.Zero, (sum, d) => sum + (d ?? TimeSpan.Zero)),
                FirstAttempt = durations[0] ?? TimeSpan.Zero,
                Attempts = test.AttemptCount
            });
        }

        timings.Sort((left, right) => right.Total.CompareTo(left.Total));

        var isolation = results.Tests
            .SelectMany(t => t.Attempts)
            .Where(a => a.Placement is AttemptPlacement.IsolatedProcess or AttemptPlacement.RecycledProcess)
            .Aggregate(TimeSpan.Zero, (sum, a) => sum + (a.Outcome.Duration ?? TimeSpan.Zero));

        var firstAttempts = results.Tests
            .Where(t => t.Attempts.Count > 0 && t.Attempts[0].Outcome.Duration is not null)
            .ToDictionary(t => t.Uid, t => t.Attempts[0].Outcome.Duration!.Value, StringComparer.Ordinal);

        return new RunTiming
        {
            RunnerSpeed = speedOf(firstAttempts, results.KnownTestDurations),
            LanePlan = lanesOf(results, firstAttempts),
            Tests = timings,
            WallClock = results.Duration,
            LaunchOverhead = results.WorkerLaunchTime,
            WorkerBusy = results.WorkerBusyTime,
            Lanes = results.Lanes,
            Unmeasured = unmeasured,
            Measured = timings.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Total),
            RetryCost = timings.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.RetryCost),
            IsolationCost = isolation
        };
    }

    private static RunnerSpeed? speedOf(
        IReadOnlyDictionary<string, TimeSpan> firstAttempts, IReadOnlyDictionary<string, TimeSpan>? known)
    {
        if (known is null || known.Count == 0) return null;

        var matched = 0;
        var thisRun = TimeSpan.Zero;
        var history = TimeSpan.Zero;
        foreach (var (uid, duration) in firstAttempts)
        {
            if (!known.TryGetValue(uid, out var median)) continue;

            matched++;
            thisRun += duration;
            history += median;
        }

        return matched > 0 && history > TimeSpan.Zero ? new RunnerSpeed(matched, thisRun, history) : null;
    }

    private static IReadOnlyList<LaneTiming> lanesOf(
        SupervisorResults results, IReadOnlyDictionary<string, TimeSpan> firstAttempts)
    {
        if (results.LanePlan.Count < 2) return [];

        // The planner's estimates are durations only when some known duration named a planned
        // test; otherwise it charged a nominal second per test, and calling that a prediction
        // would be inventing one.
        var byDuration = results.DurationCoverage is { BalancedByCount: false };

        return results.LanePlan
            .Select(lane => new LaneTiming(
                lane.Index,
                lane.Uids.Count,
                byDuration ? lane.Estimate : null,
                lane.Uids.Aggregate(TimeSpan.Zero,
                    (sum, uid) => sum + (firstAttempts.TryGetValue(uid, out var d) ? d : TimeSpan.Zero)),
                results.LaneElapsed.TryGetValue(lane.Index, out var elapsed) ? elapsed : null))
            .ToList();
    }

    /// <summary>
    /// A duration a person can read at a glance. The one definition lives on the in-process
    /// sibling (<see cref="Runtime.SuiteTiming.Humanize"/>), so the two timing reports cannot
    /// drift apart in format.
    /// </summary>
    internal static string Humanize(TimeSpan span) => Runtime.SuiteTiming.Humanize(span);

    internal static string Percent(double fraction) => Runtime.SuiteTiming.Percent(fraction);
}
