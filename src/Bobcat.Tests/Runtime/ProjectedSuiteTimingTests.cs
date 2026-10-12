using System.Text.Json;
using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;
using Spectre.Console;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #466: <see cref="SuiteTiming"/> read off projected recordings. What survives the move off
/// Gherkin -- the bracket beside the steps, per-step aggregation, gaps over the steps, scenarios that
/// assert nothing -- and what does not: the lifecycle points, which read as "not measured" and never
/// as zero (#141's rule).
/// </summary>
public class ProjectedSuiteTimingTests
{
    private static ProjectedStepSample step(string text, long start, long? end, int depth = 0,
        bool then = false, bool compares = false)
        => new(text, depth, start, end, IsThen: then, Compares: compares);

    private static ProjectedScenarioSample scenario(string title, long? wallClockMs,
        params ProjectedStepSample[] steps)
        => new("F", title, wallClockMs, steps);

    [Fact]
    public void the_bracket_and_the_steps_are_split_and_the_lifecycle_is_not_measured()
    {
        var timing = SuiteTiming.ForProjected(
        [
            scenario("slow outside its steps", 1000, step("a quick arrange", 0, 20), step("a quick check", 900, 930, then: true))
        ]);

        timing.LifecycleMeasured.ShouldBeFalse();
        timing.Lifecycle.ShouldBeEmpty();

        var s = timing.Scenarios.Single();
        s.WallClock.TotalMilliseconds.ShouldBe(1000);
        s.Steps.TotalMilliseconds.ShouldBe(50);

        // Not measured, not zero.
        s.Lifecycle.ShouldBeNull();
        s.Unowned.TotalMilliseconds.ShouldBe(950);
    }

    [Fact]
    public void gaps_rank_over_the_top_level_steps_only()
    {
        var timing = SuiteTiming.ForProjected(
        [
            scenario("gappy", 700,
                step("a slow arrange", 100, 200),
                // Inside its parent: no gap of its own, and no double counting.
                step("a nested helper", 120, 150, depth: 1),
                step("the check", 500, 600, then: true))
        ]);

        timing.Gaps.Select(g => (g.After, g.Before, g.Duration.TotalMilliseconds)).ShouldBe(
        [
            ("a slow arrange", "the check", 300d),
            ("scenario start", "a slow arrange", 100d),
            ("the check", "scenario end", 100d)
        ]);

        timing.Scenarios.Single().Steps.TotalMilliseconds.ShouldBe(200);
    }

    [Fact]
    public void steps_aggregate_by_normalised_text_at_every_depth()
    {
        var timing = SuiteTiming.ForProjected(
        [
            scenario("one", 100, step("the total is 7", 0, 30, then: true)),
            scenario("two", 100,
                step("an outer step", 0, 80),
                step("the total is 9", 10, 60, depth: 1, then: true),
                // Still running when the scenario closed: no duration, no occurrence.
                step("the label is \"Wallet\"", 60, null))
        ]);

        var totals = timing.Steps.Single(c => c.Text == "the total is {number}");
        totals.Occurrences.ShouldBe(2);
        totals.Total.TotalMilliseconds.ShouldBe(80);
        totals.Max.TotalMilliseconds.ShouldBe(50);

        timing.Steps.ShouldNotContain(c => c.Text == "the label is {string}");
    }

    [Fact]
    public void a_scenario_with_steps_but_no_assertion_is_flagged()
    {
        var timing = SuiteTiming.ForProjected(
        [
            scenario("runs but proves nothing", 50, step("a command is sent", 0, 10)),
            scenario("has a then", 50, step("the total is 7", 0, 10, then: true)),
            scenario("asserts through cells", 50, step("the rates are checked", 0, 10, compares: true)),
            // A // Then comment over the test's own assertion library is a claim Bobcat cannot
            // see as a step; flagging it would be a false positive.
            new ProjectedScenarioSample("F", "declares a then", 50, [step("a command is sent", 0, 10)],
                DeclaresThen: true),
            // No steps at all is the pending-specification hotspot, reported elsewhere.
            scenario("not written yet", 10)
        ]);

        timing.WithoutAssertions.ShouldBe(["F/runs but proves nothing"]);
    }

    [Fact]
    public void then_kind_follows_the_block_through_and_and_but()
    {
        ProjectedScenarioSample.ThenKinds(["Given", "And", "When", "Then", "And", "But", "", "Given", "And"])
            .ShouldBe([false, false, false, true, true, true, false, false, false]);
    }

    [Fact]
    public void a_recording_that_never_closed_is_unmeasured_never_zero_filled()
    {
        var timing = SuiteTiming.ForProjected(
        [
            scenario("timed", 100, step("a step", 0, 50, then: true)),
            scenario("never closed", null, step("another step", 0, 30, then: true))
        ]);

        timing.Unmeasured.ShouldBe(1);
        timing.Scenarios.Single().Title.ShouldBe("timed");
        timing.Measured.TotalMilliseconds.ShouldBe(100);
        timing.Gaps.ShouldAllBe(g => g.Scenario == "timed");
        timing.Steps.Single(c => c.Text == "another step").Occurrences.ShouldBe(1);
    }

    [Fact]
    public void real_recordings_feed_the_analysis()
    {
        var asserting = ScenarioRecorder.Begin("Projected timing", "asserts", null, Guid.NewGuid());
        using (ScenarioRecorder.Step("Given", "the total starts at 7")) { }
        ScenarioRecorder.Note("a note is not a step");
        using (ScenarioRecorder.Step("Then", "the total is 7")) { }
        using (ScenarioRecorder.Step("Then", "the label is \"Wallet\"")) { }
        asserting.Dispose();

        var vacuous = ScenarioRecorder.Begin("Projected timing", "asserts nothing", null, Guid.NewGuid());
        using (ScenarioRecorder.Step("When", "a command is sent")) { }
        vacuous.Dispose();

        var withCells = ScenarioRecorder.Begin("Projected timing", "asserts through a cell", null, Guid.NewGuid());
        using (ScenarioRecorder.Step("When", "the rates are checked"))
        {
            SpecAssert.Check("rate", 2, 2);
        }
        withCells.Dispose();

        var timing = SuiteTiming.For([asserting, vacuous, withCells]);

        timing.IsMeasured.ShouldBeTrue();
        timing.LifecycleMeasured.ShouldBeFalse();
        timing.Scenarios.Count.ShouldBe(3);
        timing.Scenarios.ShouldAllBe(x => x.Lifecycle == null && x.WallClock >= x.Steps);

        // The second Then is rendered `And`; it is still in the Then block.
        timing.WithoutAssertions.ShouldBe(["Projected timing/asserts nothing"]);
        timing.Steps.ShouldNotContain(c => c.Text == "a note is not a step");
        timing.Steps.Select(c => c.Text).ShouldContain("the label is {string}");
    }

    [Fact]
    public void the_json_says_the_lifecycle_was_not_measured_rather_than_zero()
    {
        var timing = SuiteTiming.ForProjected([scenario("timed", 100, step("a step", 0, 40, then: true))]);

        var json = JsonDocument.Parse(JsonRenderer.RenderTiming(timing)).RootElement;

        json.GetProperty("lifecycleMeasured").GetBoolean().ShouldBeFalse();
        json.TryGetProperty("lifecycle", out _).ShouldBeFalse();

        var s = json.GetProperty("scenarios")[0];
        s.GetProperty("wallClockMs").GetInt64().ShouldBe(100);
        s.GetProperty("stepsMs").GetInt64().ShouldBe(40);
        s.GetProperty("unownedMs").GetInt64().ShouldBe(60);
        s.TryGetProperty("lifecycleMs", out _).ShouldBeFalse();
    }

    [Fact]
    public void a_gherkin_suite_json_still_measures_its_lifecycle_and_says_nothing_new()
    {
        var r = new ExecutionResults("timed", DateTimeOffset.UtcNow) { WallClockMs = 100 };
        r.RecordTimelinePoint("ResetAll", 0, 40);
        var feature = new FeatureResults("F");
        feature.Add(new ScenarioResult(r.SpecId, [], r));
        var results = new SuiteResults();
        results.Add(feature);

        var json = JsonDocument.Parse(JsonRenderer.RenderSuite(results)).RootElement.GetProperty("timing");

        json.TryGetProperty("lifecycleMeasured", out _).ShouldBeFalse();
        json.GetProperty("lifecycle")[0].GetProperty("text").GetString().ShouldBe("ResetAll");
        json.GetProperty("scenarios")[0].GetProperty("lifecycleMs").GetInt64().ShouldBe(40);
    }

    [Fact]
    public void the_console_summary_says_not_measured_and_never_prints_a_zero_lifecycle()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 200;

        var timing = SuiteTiming.ForProjected(
        [
            scenario("slow outside its steps", 1000, step("a quick arrange", 0, 20), step("a quick check", 800, 830, then: true))
        ]);

        new CommandLineRenderer(console).RenderTimingSummary(timing);

        var output = writer.ToString();
        output.ShouldContain("steps 50ms, outside steps 950ms");
        output.ShouldContain("lifecycle not measured");
        output.ShouldContain("outside any step (unattributed)");
        output.ShouldNotContain("lifecycle 0ms");
    }
}
