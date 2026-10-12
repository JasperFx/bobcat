using Bobcat.Engine;

namespace Bobcat.Runtime;

public sealed partial class SuiteTiming
{
    /// <summary>
    /// Where a projected suite spent its time (issue #466), read off the recordings a marker-step
    /// suite produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What survives the move to the projected lane.</b> Each scenario's bracket wall clock
    /// (<see cref="ScenarioRecorder.Recording.WallClockMs"/>) beside what its steps cost; per-step
    /// aggregation by normalised step text; gap ranking over the steps; and the scenarios that
    /// assert nothing.
    /// </para>
    /// <para>
    /// <b>What does not, and why that is accepted.</b> There are no lifecycle points. In this lane
    /// ResetAll, BeforeEach and the rest are the test runner's lifecycle -- constructors,
    /// <c>IAsyncLifetime</c>, class and assembly fixtures -- which run outside the adapter's
    /// bracket, and Bobcat does not stamp points on a lifecycle it does not own.
    /// <see cref="LifecycleMeasured"/> is false, <see cref="Lifecycle"/> is empty, and every
    /// <see cref="ScenarioTiming.Lifecycle"/> is null. Nothing is zero-filled, and a gap is time
    /// outside any step and nothing more.
    /// </para>
    /// <para>
    /// A recording that never closed has no wall clock and counts toward
    /// <see cref="Unmeasured"/>; a withdrawn (skipped) one should not be passed at all, because
    /// <see cref="ScenarioRecorder.ScenarioCompleted"/> never raises for it.
    /// </para>
    /// </remarks>
    public static SuiteTiming For(IEnumerable<ScenarioRecorder.Recording> recordings)
        => ForProjected(recordings.Select(ProjectedScenarioSample.From));

    /// <summary>
    /// The computation behind <see cref="For(IEnumerable{ScenarioRecorder.Recording})"/>, over the
    /// light snapshot a recording is reduced to -- so a console can keep the snapshot until process
    /// exit rather than every recording, and a test can state exact offsets.
    /// </summary>
    internal static SuiteTiming ForProjected(IEnumerable<ProjectedScenarioSample> samples)
    {
        var scenarios = new List<ScenarioTiming>();
        var gaps = new List<TimelineGap>();
        var withoutAssertions = new List<string>();
        var stepCosts = new Dictionary<string, (int Occurrences, long TotalMs, long MaxMs)>();
        var unmeasured = 0;

        foreach (var sample in samples)
        {
            if (sample.Steps.Count > 0 && !sample.Asserts)
            {
                withoutAssertions.Add(SpecIdentity.Of(sample.Feature, sample.Scenario));
            }

            // Every depth: a helper called from inside another is still a grammar step with a cost
            // of its own, and "what did this sentence cost across the suite" is asked of both.
            foreach (var step in sample.Steps.Where(x => x.EndedAtMs is not null))
            {
                accumulate(stepCosts, NormalizeStepText(step.Text), step.DurationMs);
            }

            if (sample.WallClockMs is not { } wallClockMs)
            {
                unmeasured++;
                continue;
            }

            // Top level only: a nested step sits inside its parent's interval, so walking it would
            // find no gap the parent did not already cover, and summing it would double count.
            var topLevel = sample.Steps.Where(x => x.Depth == 0 && x.EndedAtMs is not null).ToList();

            collectGaps(sample.Feature, sample.Scenario,
                topLevel.Select(x => (Name: x.Text, Start: x.StartedAtMs, End: x.EndedAtMs!.Value)),
                wallClockMs, gaps);

            scenarios.Add(new ScenarioTiming(sample.Feature, sample.Scenario)
            {
                WallClock = TimeSpan.FromMilliseconds(wallClockMs),
                Steps = TimeSpan.FromMilliseconds(topLevel.Sum(x => x.DurationMs)),
                Lifecycle = null
            });
        }

        scenarios.Sort((left, right) => right.WallClock.CompareTo(left.WallClock));
        gaps.Sort((left, right) => right.Duration.CompareTo(left.Duration));

        return new SuiteTiming
        {
            Scenarios = scenarios,
            Steps = toCosts(stepCosts),
            Lifecycle = [],
            LifecycleMeasured = false,
            Gaps = gaps,
            WithoutAssertions = withoutAssertions,
            Unmeasured = unmeasured,
            Measured = scenarios.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.WallClock)
        };
    }
}

/// <summary>
/// One projected scenario reduced to what <see cref="SuiteTiming"/> reads: no exceptions, no
/// cells, no reports. Taken when the recording closes.
/// </summary>
internal sealed record ProjectedScenarioSample(
    string Feature,
    string Scenario,
    long? WallClockMs,
    IReadOnlyList<ProjectedStepSample> Steps,
    bool DeclaresThen = false)
{
    /// <summary>
    /// Whether anything in the scenario made a claim: a Then-kind step, a comparison cell, an
    /// assertion a step failed on, or a <c>// Then</c> marker comment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The declared comment counts because of how projected tests are written: a
    /// <c>// Then the total is 7</c> over a Shouldly line is an assertion Bobcat cannot see as a
    /// step, and flagging it would turn a fact into a false positive. The same rule is why an
    /// assertion written in the test body with NO Then anywhere is reported -- Bobcat sees steps
    /// and comments, not the test's own assertion library.
    /// </para>
    /// <para>
    /// <see cref="Counts.Rights"/> is not the signal: every finished step counts as a right, so
    /// rights count steps, not assertions.
    /// </para>
    /// </remarks>
    public bool Asserts => DeclaresThen || Steps.Any(x => x.IsThen || x.Compares);

    public static ProjectedScenarioSample From(ScenarioRecorder.Recording recording)
    {
        var recorded = recording.Steps
            .Where(x => !string.Equals(x.Keyword, ScenarioRecorder.NoteKeyword, StringComparison.Ordinal))
            .ToList();

        var thenKinds = ThenKinds(recorded.Select(x => x.Keyword));
        var steps = recorded
            .Select((x, i) => new ProjectedStepSample(
                x.Text, x.Depth, x.StartedAtMs, x.EndedAtMs,
                IsThen: thenKinds[i],
                Compares: x.Cells.Any(c => c.Status != ResultStatus.ok)
                          || (x.Failure is not null && ProjectedFailure.IsAssertion(x.Failure))))
            .ToList();

        return new ProjectedScenarioSample(
            recording.Feature, recording.Scenario, recording.WallClockMs, steps,
            DeclaresThen: ThenKinds(recording.Declared.Select(x => x.Keyword)).Any(x => x));
    }

    /// <summary>
    /// Which keywords sit in a Then block. The recorder renders a repeated keyword as <c>And</c>,
    /// and a helper may spell <c>And</c> or <c>But</c> itself, so a step's kind is the last real
    /// keyword before it. An empty keyword (a Storyteller-style sentence) opens no block and is not
    /// Then-kind; its cells can still make it assert.
    /// </summary>
    internal static bool[] ThenKinds(IEnumerable<string> keywords)
    {
        var kinds = new List<bool>();
        string? block = null;

        foreach (var keyword in keywords)
        {
            if (keyword.Equals("And", StringComparison.OrdinalIgnoreCase)
                || keyword.Equals("But", StringComparison.OrdinalIgnoreCase))
            {
                kinds.Add(block is not null && block.Equals("Then", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (keyword.Length > 0) block = keyword;
            kinds.Add(keyword.Equals("Then", StringComparison.OrdinalIgnoreCase));
        }

        return kinds.ToArray();
    }
}

/// <summary>One recorded step as <see cref="SuiteTiming"/> reads it.</summary>
internal sealed record ProjectedStepSample(
    string Text,
    int Depth,
    long StartedAtMs,
    long? EndedAtMs,
    bool IsThen = false,
    bool Compares = false)
{
    public long DurationMs => EndedAtMs is { } ended ? Math.Max(0, ended - StartedAtMs) : 0;
}
