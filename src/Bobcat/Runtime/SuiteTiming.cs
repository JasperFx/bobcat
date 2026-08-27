using System.Globalization;
using System.Text.RegularExpressions;
using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>One scenario's wall clock, split into what owned it.</summary>
public sealed record ScenarioTiming(string Feature, string Title)
{
    /// <summary>The whole bracket — reset, scope, hooks, steps, teardown (issue #141).</summary>
    public required TimeSpan WallClock { get; init; }

    /// <summary>What the steps themselves cost.</summary>
    public required TimeSpan Steps { get; init; }

    /// <summary>What the named lifecycle points cost — reset, scope, hooks, teardown.</summary>
    public required TimeSpan Lifecycle { get; init; }

    /// <summary>Wall clock that no step and no lifecycle point owns.</summary>
    public TimeSpan Unowned
    {
        get
        {
            var unowned = WallClock - Steps - Lifecycle;
            return unowned > TimeSpan.Zero ? unowned : TimeSpan.Zero;
        }
    }
}

/// <summary>
/// What one step text (or one lifecycle point) cost across the whole suite. For steps the key
/// is the <em>normalized</em> text — quoted strings and bare numbers replaced by placeholders —
/// because the results carry the rendered text, not the Cucumber expression, and "the total is
/// 7" and "the total is 9" are one grammar step. The normalization is deterministic
/// substitution, not a guess about timing.
/// </summary>
public sealed record StepCost(string Text, int Occurrences, TimeSpan Total, TimeSpan Max);

/// <summary>
/// A stretch of one scenario's wall clock that nothing owns: it ended when <see cref="Before"/>
/// started, and the last thing known to finish before it was <see cref="After"/>. Durations say
/// what the steps cost; gaps are what the steps did NOT cost, made computable by #141's offsets.
/// </summary>
public sealed record TimelineGap(string Feature, string Scenario, string After, string Before, TimeSpan Duration);

/// <summary>
/// Where an in-process suite spent its time — issue #142 items 1–3, computed off the #141
/// timeline. The in-process sibling of the supervisor's <c>RunTiming</c>: that one describes a
/// run of worker processes, this one looks inside the scenarios, which only Bobcat can do
/// because steps are a Bobcat concept.
/// </summary>
/// <remarks>
/// <para>
/// Pure computation over <see cref="SuiteResults"/>, so it is testable without a run and
/// renderable more than one way (<c>CommandLineRenderer.RenderTimingSummary</c>, the
/// <c>timing</c> block in <c>JsonRenderer.RenderSuite</c>).
/// </para>
/// <para>
/// <strong>Report, don't act</strong> — the guardrail carried from #56 and #44. Nothing here
/// fails a build or skips a test; whether a slow step or an assertion-free spec is a bug is a
/// judgement, and this is the evidence for making it.
/// </para>
/// <para>
/// <strong>In-process only, and honestly so.</strong> An xUnit or tUnit worker over the MTP
/// wire has no steps and never will; it degrades to nothing here, never to zero-filled
/// figures — the run-level view for those is <c>RunTiming</c>. In particular the no-assertion
/// check cannot see #56's own motivating example (a foreign test with a one-minute
/// <c>Task.Delay</c> and no assertions), because that arrives as a bare duration on the wire.
/// </para>
/// <para>
/// Figures describe each scenario's <em>final</em> attempt — <see cref="ScenarioResult.Results"/>
/// is the only attempt whose <see cref="ExecutionResults"/> survives in-process. What retries
/// cost is <c>RunTiming.RetryCost</c>'s question.
/// </para>
/// </remarks>
public sealed partial class SuiteTiming
{
    private SuiteTiming()
    {
    }

    /// <summary>Every measured scenario, slowest first.</summary>
    public IReadOnlyList<ScenarioTiming> Scenarios { get; private init; } = [];

    /// <summary>
    /// What each grammar step cost across the suite, costliest first — "Given events for
    /// {aggregate} cost 90s across 200 scenarios" is a fact about the grammar, not about any
    /// one spec, and no profiler can say it.
    /// </summary>
    public IReadOnlyList<StepCost> Steps { get; private init; } = [];

    /// <summary>
    /// What each lifecycle point cost across the suite, costliest first. In a database-backed
    /// suite the ResetAll line is plausibly the largest single finding in the whole report.
    /// </summary>
    public IReadOnlyList<StepCost> Lifecycle { get; private init; } = [];

    /// <summary>Time no stop point owns, largest first — a subtraction, zero false positives.</summary>
    public IReadOnlyList<TimelineGap> Gaps { get; private init; } = [];

    /// <summary>
    /// Scenarios ("Feature/Scenario") that ran steps but asserted nothing: no Then step and no
    /// comparison cell anywhere. A fact, not a heuristic — Bobcat built the plan. A scenario
    /// with no steps at all is deliberately absent: that is the pending-specification hotspot
    /// (#106), already reported elsewhere.
    /// </summary>
    public IReadOnlyList<string> WithoutAssertions { get; private init; } = [];

    /// <summary>
    /// Scenarios whose results carried no wall clock — an older artifact, a bare executor.
    /// Never zero-filled: unmeasured is not free, and the figures above are a floor.
    /// </summary>
    public int Unmeasured { get; private init; }

    /// <summary>Every measured scenario's wall clock, added up.</summary>
    public TimeSpan Measured { get; private init; }

    public bool IsMeasured => Scenarios.Count > 0;

    /// <summary>What fraction of the measured time a span accounts for. Null when nothing was measured.</summary>
    public double? Share(TimeSpan span)
        => Measured > TimeSpan.Zero ? span / Measured : null;

    public IReadOnlyList<ScenarioTiming> Slowest(int count) => Scenarios.Take(count).ToList();

    public static SuiteTiming For(SuiteResults results)
    {
        var scenarios = new List<ScenarioTiming>();
        var gaps = new List<TimelineGap>();
        var withoutAssertions = new List<string>();
        var stepCosts = new Dictionary<string, (int Occurrences, long TotalMs, long MaxMs)>();
        var lifecycleCosts = new Dictionary<string, (int Occurrences, long TotalMs, long MaxMs)>();
        var unmeasured = 0;

        foreach (var feature in results.Features)
        {
            foreach (var scenario in feature.Scenarios)
            {
                var r = scenario.Results;

                if (r.Steps.Count > 0 && assertsNothing(r))
                {
                    withoutAssertions.Add($"{feature.Title}/{scenario.Title}");
                }

                foreach (var step in r.Steps)
                {
                    accumulate(stepCosts, NormalizeStepText(step.StepText ?? step.StepId),
                        Math.Max(0, step.End - step.Start));
                }

                foreach (var point in r.Timeline)
                {
                    accumulate(lifecycleCosts, point.Name, point.DurationMs);
                }

                // A sub-millisecond scenario legitimately measures 0ms, so a bare zero is not
                // the "no clock" sentinel: results from the runner's bracket always carry
                // timeline points, and results with neither came from something that never had
                // the #141 clock — a bare executor, an older artifact.
                if (r.WallClockMs <= 0 && r.Timeline.Count == 0)
                {
                    unmeasured++;
                    continue;
                }

                collectGaps(feature.Title, scenario.Title, r, gaps);

                scenarios.Add(new ScenarioTiming(feature.Title, scenario.Title)
                {
                    WallClock = TimeSpan.FromMilliseconds(r.WallClockMs),
                    Steps = TimeSpan.FromMilliseconds(r.Steps.Sum(s => Math.Max(0, s.End - s.Start))),
                    Lifecycle = TimeSpan.FromMilliseconds(r.Timeline.Sum(p => p.DurationMs))
                });
            }
        }

        scenarios.Sort((left, right) => right.WallClock.CompareTo(left.WallClock));
        gaps.Sort((left, right) => right.Duration.CompareTo(left.Duration));

        return new SuiteTiming
        {
            Scenarios = scenarios,
            Steps = toCosts(stepCosts),
            Lifecycle = toCosts(lifecycleCosts),
            Gaps = gaps,
            WithoutAssertions = withoutAssertions,
            Unmeasured = unmeasured,
            Measured = scenarios.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.WallClock)
        };
    }

    /// <summary>
    /// Walk one scenario's known intervals — steps and lifecycle points, all on the same clock
    /// with the same zero (#141) — and record every stretch nothing covers, attributed by its
    /// neighbours: "3.1s between 'the events are committed' and 'the read model contains'" is
    /// actionable where "3.1s unaccounted" is a mystery.
    /// </summary>
    private static void collectGaps(string feature, string scenario, ExecutionResults r, List<TimelineGap> gaps)
    {
        var intervals = r.Timeline.Select(p => (p.Name, Start: p.StartMs, End: p.EndMs))
            .Concat(r.Steps.Select(s => (Name: s.StepText ?? s.StepId, s.Start, s.End)))
            .OrderBy(i => i.Start)
            .ToList();

        var cursor = 0L;
        var lastName = "scenario start";

        foreach (var interval in intervals)
        {
            if (interval.Start > cursor)
            {
                gaps.Add(new TimelineGap(feature, scenario, lastName, interval.Name,
                    TimeSpan.FromMilliseconds(interval.Start - cursor)));
            }

            if (interval.End >= cursor)
            {
                cursor = interval.End;
                lastName = interval.Name;
            }
        }

        if (r.WallClockMs > cursor)
        {
            gaps.Add(new TimelineGap(feature, scenario, lastName, "scenario end",
                TimeSpan.FromMilliseconds(r.WallClockMs - cursor)));
        }
    }

    /// <summary>
    /// No Then-kind step (the grammar's assertion slot — [Then] and [Check] both bind there)
    /// and no comparison cell (a decision table or set verification asserts through cells on
    /// whatever keyword carries it; input/echo cells stay <see cref="ResultStatus.ok"/> and do
    /// not count). <see cref="Counts.Rights"/> is deliberately not the signal: the executor
    /// auto-marks every completed step <c>success</c>, so rights count steps, not assertions.
    /// </summary>
    private static bool assertsNothing(ExecutionResults results)
        => results.Steps.All(s => s.StepKind != StepKind.Then
                                  && s.Cells.All(c => c.Status == ResultStatus.ok));

    private static void accumulate(
        Dictionary<string, (int Occurrences, long TotalMs, long MaxMs)> costs, string key, long durationMs)
    {
        var current = costs.GetValueOrDefault(key);
        costs[key] = (current.Occurrences + 1, current.TotalMs + durationMs, Math.Max(current.MaxMs, durationMs));
    }

    private static List<StepCost> toCosts(Dictionary<string, (int Occurrences, long TotalMs, long MaxMs)> costs)
        => costs
            .Select(kv => new StepCost(kv.Key, kv.Value.Occurrences,
                TimeSpan.FromMilliseconds(kv.Value.TotalMs), TimeSpan.FromMilliseconds(kv.Value.MaxMs)))
            .OrderByDescending(c => c.Total)
            .ToList();

    /// <summary>
    /// Fold the rendered argument values out of a step's text so occurrences of one grammar
    /// step group together: quoted strings become {string}, bare numbers {number}. Word-embedded
    /// digits (sha256, utf8) are left alone, as is anything already in braces.
    /// </summary>
    public static string NormalizeStepText(string text)
        => bareNumber().Replace(quotedString().Replace(text, "{string}"), "{number}");

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex quotedString();

    [GeneratedRegex(@"(?<![\w{.-])-?\d+(?:\.\d+)?(?![\w}.])")]
    private static partial Regex bareNumber();

    /// <summary>
    /// A duration a person can read at a glance. Invariant on purpose — a CI log should not
    /// change shape with the agent's locale. Shared with the supervisor's <c>RunTiming</c>.
    /// </summary>
    public static string Humanize(TimeSpan span) => span.TotalSeconds switch
    {
        < 1 => span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms",
        < 60 => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s",
        // The remainder rather than Seconds, so 60.9s reads as "1m 1s" rather than "1m 0s".
        _ => $"{(int)span.TotalMinutes}m {(span.TotalSeconds % 60).ToString("0", CultureInfo.InvariantCulture)}s"
    };

    public static string Percent(double fraction)
        => (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
