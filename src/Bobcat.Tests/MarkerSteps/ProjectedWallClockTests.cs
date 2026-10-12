using System.Text.Json;
using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Shouldly;
using Spectre.Console;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// Issue #466: a projected scenario reports its bracket's wall clock separately from what its steps
/// cost, so a scenario whose time sits outside its steps reads honestly rather than as a fast one.
/// Durations are asserted structurally and with wide margins -- the point is which clock a figure
/// came from, not the nominal length of a sleep.
/// </summary>
public class ProjectedWallClockTests
{
    private readonly RecordingSink _sink = new();

    private ScenarioRecorder.Recording begin(string scenario = "Scenario")
        => ScenarioRecorder.Begin("Wall clock", scenario, _sink, Guid.NewGuid());

    [Fact]
    public void the_wall_clock_is_unknown_while_open_and_stamped_when_the_scenario_closes()
    {
        var recording = begin();
        using (ScenarioRecorder.Step("Given", "a step")) { }

        recording.WallClockMs.ShouldBeNull();

        recording.Dispose();

        recording.WallClockMs.ShouldNotBeNull();
        recording.WallClockMs!.Value.ShouldBeGreaterThanOrEqualTo(recording.StepsMs);
    }

    [Fact]
    public void time_outside_the_steps_shows_in_the_bracket_and_not_in_the_steps()
    {
        var recording = begin();
        using (ScenarioRecorder.Step("Given", "an instant arrange")) { }

        // The test body doing work no step owns -- the case that used to read as a fast scenario.
        Thread.Sleep(150);

        using (ScenarioRecorder.Step("Then", "an instant check")) { }
        recording.Dispose();

        (recording.WallClockMs!.Value - recording.StepsMs).ShouldBeGreaterThanOrEqualTo(100);

        var render = SpecRender.FromRecording(recording);
        render.DurationMs.ShouldBe(recording.WallClockMs.Value);
        render.StepsMs.ShouldBe(recording.StepsMs);

        var finished = _sink.Events.OfType<ScenarioFinished>().ShouldHaveSingleItem();
        finished.DurationMs.ShouldBe(recording.WallClockMs.Value);
        finished.StepsMs.ShouldBe(recording.StepsMs);
    }

    [Fact]
    public void a_nested_step_is_inside_its_parent_and_is_not_counted_twice()
    {
        var recording = begin();
        using (ScenarioRecorder.Step("Given", "an outer step"))
        using (ScenarioRecorder.Step("Given", "an inner step"))
        {
            Thread.Sleep(60);
        }

        recording.Dispose();

        var outer = recording.Steps.Single(x => x.Depth == 0);
        recording.StepsMs.ShouldBe(outer.DurationMs!.Value);
    }

    [Fact]
    public void a_recording_rendered_before_it_closes_falls_back_to_the_step_span_and_claims_no_step_figure()
    {
        using var recording = begin();
        using (ScenarioRecorder.Step("Given", "a step")) { }

        var render = SpecRender.FromRecording(recording);

        render.StepsMs.ShouldBeNull();
        render.DurationMs.ShouldBe(recording.Steps.Single().EndedAtMs!.Value);
    }

    [Fact]
    public void a_withdrawn_scenario_has_no_wall_clock()
    {
        var recording = begin();
        recording.Cancel();

        recording.WallClockMs.ShouldBeNull();
        _sink.Events.OfType<ScenarioFinished>().ShouldBeEmpty();
    }

    [Fact]
    public void the_scenario_json_carries_the_step_figure_beside_the_duration()
    {
        var recording = begin();
        using (ScenarioRecorder.Step("Given", "a step")) { }
        recording.Dispose();

        var json = JsonDocument.Parse(JsonRenderer.RenderScenario(SpecRender.FromRecording(recording))).RootElement;

        json.GetProperty("durationMs").GetInt64().ShouldBe(recording.WallClockMs!.Value);
        json.GetProperty("stepsMs").GetInt64().ShouldBe(recording.StepsMs);
    }

    [Fact]
    public void the_console_prints_the_step_figure_beside_the_duration()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        new CommandLineRenderer(console).Render(new SpecRender
        {
            Title = "timed", FeatureTitle = "Wall clock", Succeeded = true, DurationMs = 900, StepsMs = 40
        });

        writer.ToString().ShouldContain("Duration: 900ms (steps 40ms)");
    }

    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = new();

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }
}
