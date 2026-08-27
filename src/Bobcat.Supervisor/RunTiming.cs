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

        return new RunTiming
        {
            Tests = timings,
            WallClock = results.Duration,
            LaunchOverhead = results.WorkerLaunchTime,
            Unmeasured = unmeasured,
            Measured = timings.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Total),
            RetryCost = timings.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.RetryCost),
            IsolationCost = isolation
        };
    }

    /// <summary>
    /// A duration a person can read at a glance. The one definition lives on the in-process
    /// sibling (<see cref="Runtime.SuiteTiming.Humanize"/>), so the two timing reports cannot
    /// drift apart in format.
    /// </summary>
    internal static string Humanize(TimeSpan span) => Runtime.SuiteTiming.Humanize(span);

    internal static string Percent(double fraction) => Runtime.SuiteTiming.Percent(fraction);
}
