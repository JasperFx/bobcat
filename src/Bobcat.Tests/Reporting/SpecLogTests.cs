using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Reporting;

/// <summary>
/// Step-level log lines (bobcat#547): <see cref="SpecLog.Write"/> records onto the step in progress,
/// or the scenario between steps, latched like reports and capped per step.
/// </summary>
[Collection(ReportVisibilityCollection.Name)]
public class SpecLogTests : IDisposable
{
    private readonly RecordingSink _sink = new();

    public SpecLogTests() => ScenarioReportVisibility.Reset();

    public void Dispose() => ScenarioReportVisibility.Reset();

    private ScenarioRecorder.Recording begin()
        => ScenarioRecorder.Begin("Spec logs", "a scenario that logs", _sink, Guid.NewGuid());

    private static void logThroughTwoSteps()
    {
        SpecLog.Write("before any step");

        using (ScenarioRecorder.Step("When", "the order is placed"))
        {
            SpecLog.Write("posted /orders");
            SpecLog.Write("got 202");
        }

        using (ScenarioRecorder.Step("Then", "the order is confirmed"))
        {
        }
    }

    [Fact]
    public void lines_land_on_the_step_in_progress_and_between_steps_on_the_scenario()
    {
        using var recording = begin();
        logThroughTwoSteps();

        recording.Steps[0].Log.Lines.ShouldBe(["posted /orders", "got 202"]);
        recording.Steps[1].Log.IsEmpty.ShouldBeTrue();
        recording.Log.Lines.ShouldBe(["before any step"]);
    }

    [Fact]
    public void a_nested_step_takes_the_lines_written_inside_it()
    {
        using var recording = begin();

        using (ScenarioRecorder.Step("When", "outer"))
        {
            SpecLog.Write("outer before");
            using (ScenarioRecorder.Step("", "inner")) SpecLog.Write("inner");
            SpecLog.Write("outer after");
        }

        recording.Steps[0].Log.Lines.ShouldBe(["outer before", "outer after"]);
        recording.Steps[1].Log.Lines.ShouldBe(["inner"]);
    }

    [Fact]
    public void writing_with_no_scenario_open_does_nothing_and_never_throws()
    {
        ScenarioRecorder.Current.ShouldBeNull();
        SpecLog.IsRecording.ShouldBeFalse();

        Should.NotThrow(() => SpecLog.Write("nobody is listening"));
        Should.NotThrow(() => SpecLog.Write(null));
    }

    [Fact]
    public void a_step_keeps_the_cap_and_counts_the_rest()
    {
        using var recording = begin();

        using (ScenarioRecorder.Step("When", "a chatty step"))
        {
            for (var i = 0; i < StepLog.MaxLines + 7; i++) SpecLog.Write($"line {i}");
        }

        var log = recording.Steps[0].Log;
        log.Lines.Count.ShouldBe(StepLog.MaxLines);
        log.Lines[^1].ShouldBe($"line {StepLog.MaxLines - 1}");
        log.Suppressed.ShouldBe(7);
    }

    [Fact]
    public void a_failed_scenario_renders_its_logs_under_the_step_and_after_the_steps()
    {
        SpecRender spec;
        using (var recording = begin())
        {
            logThroughTwoSteps();
            recording.FailureDescription = "the order was never confirmed";
            spec = SpecRender.FromRecording(recording);
        }

        spec.Steps[0].Logs.ShouldBe(["posted /orders", "got 202"]);
        spec.Logs.ShouldBe(["before any step"]);

        var text = PlainSpecText.Render(spec);
        text.ShouldContain("Logs:");
        text.ShouldContain("posted /orders");
        text.ShouldContain("before any step");
        text.IndexOf("posted /orders", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("the order is confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void a_passing_scenario_shows_no_logs_until_the_run_asks_for_verbose()
    {
        SpecRender quiet;
        using (var recording = begin())
        {
            logThroughTwoSteps();
            quiet = SpecRender.FromRecording(recording);
        }

        quiet.Succeeded.ShouldBeTrue();
        quiet.Steps.ShouldAllBe(x => x.Logs.Count == 0);
        quiet.Logs.ShouldBeEmpty();
        PlainSpecText.Render(quiet).ShouldNotContain("posted /orders");
        _sink.Events.OfType<ScenarioFinished>().Single().Logs.ShouldBeNull();

        ScenarioReportVisibility.Verbose = true;

        SpecRender verbose;
        using (var recording = begin())
        {
            logThroughTwoSteps();
            verbose = SpecRender.FromRecording(recording);
        }

        verbose.Steps[0].Logs.ShouldBe(["posted /orders", "got 202"]);
        _sink.Events.OfType<ScenarioFinished>().Last().Logs.ShouldNotBeNull();
    }

    [Fact]
    public void the_json_report_carries_step_and_scenario_logs_and_the_suppressed_count()
    {
        SpecRender spec;
        using (var recording = begin())
        {
            SpecLog.Write("between steps");
            using (ScenarioRecorder.Step("When", "a chatty step"))
            {
                for (var i = 0; i < StepLog.MaxLines + 2; i++) SpecLog.Write($"line {i}");
            }

            recording.FailureDescription = "failed";
            spec = SpecRender.FromRecording(recording);
        }

        var json = JsonRenderer.RenderScenario(spec);
        json.ShouldContain("\"logs\"");
        json.ShouldContain("\"line 0\"");
        json.ShouldContain("\"suppressedLogs\": 2");
        json.ShouldContain("\"between steps\"");
    }

    [Fact]
    public void a_capped_log_says_how_many_lines_it_dropped_in_the_console()
    {
        SpecRender spec;
        using (var recording = begin())
        {
            using (ScenarioRecorder.Step("When", "a chatty step"))
            {
                for (var i = 0; i < StepLog.MaxLines + 1; i++) SpecLog.Write($"line {i}");
            }

            recording.FailureDescription = "failed";
            spec = SpecRender.FromRecording(recording);
        }

        PlainSpecText.Render(spec).ShouldContain("…and 1 more line");
    }

    [Fact]
    public void a_failed_scenario_publishes_its_logs_on_scenario_finished_by_step()
    {
        using (var recording = begin())
        {
            logThroughTwoSteps();
            recording.FailureDescription = "the order was never confirmed";
        }

        // Not on step_finished: the verdict that latches them is only known at the close.
        _sink.Events.OfType<StepFinished>().ShouldAllBe(x => x.Logs == null);

        var logs = _sink.Events.OfType<ScenarioFinished>().Single().Logs.ShouldNotBeNull();
        logs.Count.ShouldBe(2);

        logs[0].StepId.ShouldBe("s1");
        logs[0].Lines.ShouldBe(["posted /orders", "got 202"]);
        logs[0].Suppressed.ShouldBe(0);

        logs[1].StepId.ShouldBeNull();
        logs[1].Lines.ShouldBe(["before any step"]);
    }

    [Fact]
    public void a_failed_scenario_that_logged_nothing_publishes_null()
    {
        using (var recording = begin())
        {
            using (ScenarioRecorder.Step("Then", "nothing logs")) { }
            recording.FailureDescription = "failed";
        }

        _sink.Events.OfType<ScenarioFinished>().Single().Logs.ShouldBeNull();
    }

    [Fact]
    public void runner_output_gets_the_logs_with_the_scenario()
    {
        var lines = new List<string>();
        using (SpecOutput.Open(lines.Add))
        using (var recording = begin())
        {
            logThroughTwoSteps();
            recording.FailureDescription = "failed";
        }

        lines.ShouldContain(x => x.Contains("posted /orders"));
        lines.ShouldContain(x => x.Contains("before any step"));
    }

    [Fact]
    public void the_log_lines_alone_name_the_step_they_came_from()
    {
        SpecRender spec;
        using (var recording = begin())
        {
            logThroughTwoSteps();
            recording.FailureDescription = "failed";
            spec = SpecRender.FromRecording(recording);
        }

        var lines = PlainSpecText.LogLines(spec);
        lines.ShouldContain("Logs from: When the order is placed");
        lines.ShouldContain("  posted /orders");
        lines.ShouldContain("Logs:");
        lines.ShouldContain("  before any step");
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
