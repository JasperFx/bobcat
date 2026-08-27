using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #141 — the per-scenario step timeline. One wall clock per attempt, zeroed at the
/// ScenarioStarted announcement; step offsets stamped from it; the lifecycle work that is not
/// a step (the reset/scope bracket, BeforeEach/AfterEach, teardown) captured as named stop
/// points on the same clock; and the scenario's reported duration the bracket's real wall
/// clock rather than the last step's end. No analysis here — that is #142.
/// </summary>
public class StepTimelineTests
{
    public class TimelineFixture : Fixture;

    private static async Task<ExecutionResults> run(FeatureDefinition feature)
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(feature);
        var results = await runner.RunAll();
        return results.AllScenarios.Single().Results;
    }

    private static FeatureDefinition timedFeature(bool withHooks)
    {
        var scenario = new ScenarioDefinition("timed", [], (_, plan) =>
        {
            plan.Add(new DelegateExecutionStep("s1", StepKind.Given, "a slow arrange",
                async (_, _, _) => await Task.Delay(20)));
            plan.Add(new DelegateExecutionStep("s2", StepKind.Then, "a quick check",
                (_, _, _) => Task.CompletedTask));
        });

        return new FeatureDefinition("Timeline", typeof(TimelineFixture), [scenario])
        {
            BeforeEach = withHooks ? async (_, _) => await Task.Delay(25) : null,
            AfterEach = withHooks ? (_, _) => Task.CompletedTask : null
        };
    }

    [Fact]
    public async Task lifecycle_work_becomes_named_stop_points_on_the_scenario_clock()
    {
        var results = await run(timedFeature(withHooks: true));

        results.Timeline.Select(p => p.Name).ShouldBe(
            ["ResetAll", "BeginScenarioAll", "BeforeEach", "AfterEach", "EndScenarioAll"]);

        // "BeforeEach 25ms" is a diagnostic; "25ms unaccounted" is a mystery.
        var beforeEach = results.Timeline.Single(p => p.Name == "BeforeEach");
        beforeEach.DurationMs.ShouldBeGreaterThanOrEqualTo(20);

        // Everything shares one zero, so the timeline is ordered by construction: the first
        // step starts at or after the BeforeEach that preceded it.
        results.Steps[0].Start.ShouldBeGreaterThanOrEqualTo(beforeEach.EndMs);
    }

    [Fact]
    public async Task hooks_that_do_not_exist_produce_no_stop_points()
    {
        var results = await run(timedFeature(withHooks: false));

        results.Timeline.Select(p => p.Name).ShouldBe(
            ["ResetAll", "BeginScenarioAll", "EndScenarioAll"]);
    }

    [Fact]
    public async Task the_wall_clock_covers_the_whole_bracket_not_just_the_steps()
    {
        var results = await run(timedFeature(withHooks: true));

        var lastStepEnd = results.Steps.Max(s => s.End);

        // max(step.End) is exactly what used to be reported as the scenario duration, and it
        // structurally excludes the lifecycle time this issue exists to expose.
        results.WallClockMs.ShouldBeGreaterThanOrEqualTo(lastStepEnd);
        results.WallClockMs.ShouldBeGreaterThanOrEqualTo(
            results.Timeline.Single(p => p.Name == "EndScenarioAll").EndMs);
    }

    [Fact]
    public async Task the_render_model_keeps_offsets_and_reports_the_true_duration()
    {
        var results = await run(timedFeature(withHooks: true));
        var render = SpecRender.FromResults("timed", results, "Timeline");

        render.DurationMs.ShouldBe(results.WallClockMs);
        render.Steps.Select(s => s.StartedAtMs).ShouldBe(results.Steps.Select(s => s.Start).ToList());
        render.Timeline.Select(p => p.Name).ShouldBe(results.Timeline.Select(p => p.Name).ToList());
    }

    [Fact]
    public void results_without_a_wall_clock_fall_back_to_the_last_step_end()
    {
        // A bare executor (or an older artifact) never had the bracket clock; the fallback
        // under-reports honestly rather than inventing a number.
        var results = new ExecutionResults("bare", DateTimeOffset.UtcNow);
        results.StartStep("s1", 5).MarkEnded(40);

        SpecRender.FromResults("bare", results).DurationMs.ShouldBe(40);
    }

    [Fact]
    public async Task the_json_artifact_holds_the_timeline()
    {
        var results = await run(timedFeature(withHooks: true));
        var json = JsonRenderer.RenderScenario(SpecRender.FromResults("timed", results, "Timeline"));

        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;

        var lifecycle = root.GetProperty("lifecycle").EnumerateArray().ToArray();
        lifecycle.Select(p => p.GetProperty("name").GetString()).ShouldBe(
            ["ResetAll", "BeginScenarioAll", "BeforeEach", "AfterEach", "EndScenarioAll"]);
        lifecycle[0].TryGetProperty("startedAtMs", out _).ShouldBeTrue();

        var steps = root.GetProperty("steps").EnumerateArray().ToArray();
        steps[0].GetProperty("startedAtMs").GetInt64().ShouldBe(results.Steps[0].Start);
        root.GetProperty("durationMs").GetInt64().ShouldBe(results.WallClockMs);
    }
}
