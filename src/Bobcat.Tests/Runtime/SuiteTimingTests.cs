using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #142, items 1–3 — the analyses that need nothing but #141's timeline: gap ranking (a
/// subtraction, zero false positives), per-step aggregation by normalized text (a fact about
/// the grammar), and specs that assert nothing (a fact about the plan). Report, don't act:
/// nothing here fails a build, and unmeasured is never zero-filled.
/// </summary>
public class SuiteTimingTests
{
    private static SuiteResults suite(params (string Feature, ExecutionResults Results)[] scenarios)
    {
        var results = new SuiteResults();
        foreach (var group in scenarios.GroupBy(s => s.Feature))
        {
            var feature = new FeatureResults(group.Key);
            foreach (var (_, r) in group)
            {
                feature.Add(new ScenarioResult(r.SpecId, [], r));
            }

            results.Add(feature);
        }

        return results;
    }

    private static ExecutionResults scenario(string title, long wallClockMs = 0)
        => new(title, DateTimeOffset.UtcNow) { WallClockMs = wallClockMs };

    private static StepResult step(ExecutionResults r, string text, long start, long end,
        StepKind kind = StepKind.Given)
    {
        var result = r.StartStep(Guid.NewGuid().ToString(), start, kind);
        result.StepText = text;
        result.MarkSuccess();
        result.MarkEnded(end);
        return result;
    }

    [Fact]
    public void gaps_are_subtractions_attributed_by_their_neighbours()
    {
        var r = scenario("gappy", wallClockMs: 700);
        r.RecordTimelinePoint("ResetAll", 0, 40);
        step(r, "a slow arrange", 100, 200);
        step(r, "the check", 500, 600, StepKind.Then);

        var timing = SuiteTiming.For(suite(("F", r)));

        // Largest first: 300ms between the steps, 100ms trailing, 60ms after ResetAll.
        timing.Gaps.Select(g => (g.After, g.Before, g.Duration.TotalMilliseconds)).ShouldBe(
        [
            ("a slow arrange", "the check", 300d),
            ("the check", "scenario end", 100d),
            ("ResetAll", "a slow arrange", 60d)
        ]);

        var s = timing.Scenarios.Single();
        s.WallClock.TotalMilliseconds.ShouldBe(700);
        s.Steps.TotalMilliseconds.ShouldBe(200);
        s.Lifecycle.TotalMilliseconds.ShouldBe(40);
        s.Unowned.TotalMilliseconds.ShouldBe(460);
    }

    [Fact]
    public void steps_aggregate_by_normalized_text_across_the_suite()
    {
        var first = scenario("one", wallClockMs: 100);
        step(first, "the total is 7", 0, 30, StepKind.Then);
        var second = scenario("two", wallClockMs: 100);
        step(second, "the total is 9", 0, 50, StepKind.Then);
        step(second, "the label is \"Wallet\"", 50, 60, StepKind.Then);

        var timing = SuiteTiming.For(suite(("F", first), ("F", second)));

        // "the total is 7" and "the total is 9" are one grammar step — the results carry
        // rendered text, so the argument values are folded out deterministically.
        var totals = timing.Steps.Single(c => c.Text == "the total is {number}");
        totals.Occurrences.ShouldBe(2);
        totals.Total.TotalMilliseconds.ShouldBe(80);
        totals.Max.TotalMilliseconds.ShouldBe(50);

        timing.Steps.Single(c => c.Text == "the label is {string}").Occurrences.ShouldBe(1);
    }

    [Theory]
    [InlineData("waits 30 seconds", "waits {number} seconds")]
    [InlineData("the amount is -2.5", "the amount is {number}")]
    [InlineData("hashed with sha256", "hashed with sha256")] // word-embedded digits stay
    [InlineData("version 1.2.3 is published", "version 1.2.3 is published")] // dotted versions stay
    public void normalization_folds_arguments_without_mangling_words(string text, string expected)
    {
        SuiteTiming.NormalizeStepText(text).ShouldBe(expected);
    }

    [Fact]
    public void lifecycle_points_aggregate_by_name()
    {
        var first = scenario("one", wallClockMs: 100);
        first.RecordTimelinePoint("ResetAll", 0, 40);
        var second = scenario("two", wallClockMs: 100);
        second.RecordTimelinePoint("ResetAll", 0, 60);
        second.RecordTimelinePoint("BeforeEach", 60, 70);

        var timing = SuiteTiming.For(suite(("F", first), ("F", second)));

        var reset = timing.Lifecycle.Single(c => c.Text == "ResetAll");
        reset.Occurrences.ShouldBe(2);
        reset.Total.TotalMilliseconds.ShouldBe(100);
        // Costliest first: in a database-backed suite the ResetAll line is plausibly the
        // largest single finding in the report.
        timing.Lifecycle[0].Text.ShouldBe("ResetAll");
    }

    [Fact]
    public void a_scenario_with_steps_but_no_assertion_is_flagged_as_a_fact()
    {
        var vacuous = scenario("runs but proves nothing", wallClockMs: 50);
        step(vacuous, "a command is sent", 0, 10, StepKind.When);

        var asserting = scenario("has a then", wallClockMs: 50);
        step(asserting, "the total is 7", 0, 10, StepKind.Then);

        // A decision table asserts through cells on whatever keyword carries it.
        var table = scenario("asserts through cells", wallClockMs: 50);
        step(table, "the rates are checked", 0, 10, StepKind.When)
            .MarkCells(new CellResult("rate", ResultStatus.success) { Expected = "2", Actual = "2" });

        // Input/echo cells are not comparisons.
        var echoOnly = scenario("echoes input", wallClockMs: 50);
        step(echoOnly, "these rows are loaded", 0, 10, StepKind.Given)
            .MarkCells(new CellResult("name", ResultStatus.ok, "Wallet"));

        // A scenario with no steps at all is the pending-specification hotspot (#106),
        // reported elsewhere — not this list.
        var pending = scenario("not written yet", wallClockMs: 10);

        var timing = SuiteTiming.For(suite(
            ("F", vacuous), ("F", asserting), ("F", table), ("F", echoOnly), ("F", pending)));

        timing.WithoutAssertions.ShouldBe(
            ["F/runs but proves nothing", "F/echoes input"]);
    }

    [Fact]
    public void unmeasured_scenarios_are_counted_never_zero_filled()
    {
        var measured = scenario("timed", wallClockMs: 100);
        step(measured, "a step", 0, 50, StepKind.Then);

        // A bare executor or an older artifact: steps but no bracket wall clock.
        var bare = scenario("untimed");
        step(bare, "another step", 0, 30, StepKind.Then);

        var timing = SuiteTiming.For(suite(("F", measured), ("F", bare)));

        timing.Unmeasured.ShouldBe(1);
        timing.Scenarios.Single().Title.ShouldBe("timed");
        timing.Measured.TotalMilliseconds.ShouldBe(100);
        // No gap is invented for the unmeasured one; its steps still feed the grammar totals.
        timing.Gaps.ShouldAllBe(g => g.Scenario == "timed");
        timing.Steps.Single(c => c.Text == "another step").Occurrences.ShouldBe(1);
    }

    [Fact]
    public void an_empty_suite_has_nothing_to_say()
    {
        var timing = SuiteTiming.For(suite());
        timing.IsMeasured.ShouldBeFalse();
        timing.Share(TimeSpan.FromSeconds(1)).ShouldBeNull();
        timing.WithoutAssertions.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_runner_end_to_end_feeds_the_analysis()
    {
        var scenarioDefinition = new ScenarioDefinition("timed", [], (_, plan) =>
        {
            plan.Add(new DelegateExecutionStep("s1", StepKind.Then, "a real check",
                async (_, _, _) => await Task.Delay(20)));
        });
        var feature = new FeatureDefinition("Timing", typeof(StepTimelineTests.TimelineFixture), [scenarioDefinition]);

        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(feature);
        var results = await runner.RunAll();

        var timing = SuiteTiming.For(results);

        timing.IsMeasured.ShouldBeTrue();
        timing.Lifecycle.Select(c => c.Text).ShouldContain("ResetAll");
        timing.Steps.Single().Text.ShouldBe("a real check");
        timing.WithoutAssertions.ShouldBeEmpty();
        timing.Scenarios.Single().WallClock.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(20));
    }
}
