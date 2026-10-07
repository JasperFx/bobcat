using Bobcat.Engine;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Reporting;

public class TextGridTests
{
    private static ReportRender aGrid()
    {
        var report = new MessageActivityReport();
        report.Record(0, "Sent", "ConfirmAppointment", "local://confirm", 1);
        report.DeadLettered(104, "NotifyPatient", "rabbitmq://notify", 3);
        return ReportRender.From(report);
    }

    [Fact]
    public void the_heading_then_the_columns_then_a_rule()
    {
        var lines = TextGrid.Render(aGrid());

        lines[0].ShouldBe("Message activity");
        lines[1].ShouldStartWith("at (ms) | event");
        lines[2].ShouldStartWith("------- | ");
    }

    [Fact]
    public void columns_are_padded_to_one_width_so_the_grid_lines_up()
    {
        var lines = TextGrid.Render(aGrid());

        // Every row has the separator in the same place, which is the whole point of a fixed-width
        // grid for both readers — a person scanning and a model tokenizing.
        var positions = lines.Skip(1).Select(l => l.IndexOf('|')).Distinct().ToList();
        positions.ShouldHaveSingleItem();
    }

    [Fact]
    public void the_row_that_disagreed_is_marked_in_text_rather_than_in_colour()
    {
        var lines = TextGrid.Render(aGrid());

        // Findable by eye in a CI log and by grep, neither of which sees an ANSI code.
        lines.Count(l => l.EndsWith("<-- FAILED")).ShouldBe(1);
        lines.Single(l => l.EndsWith("<-- FAILED")).ShouldContain("MovedToErrorQueue");
    }

    [Fact]
    public void a_capped_report_says_how_many_rows_it_dropped()
    {
        var capped = new CappedReport();
        for (var n = 0; n < 10; n++) capped.Add(n);

        TextGrid.Render(ReportRender.From(capped)).Last()
            .ShouldBe("…and 7 more rows not shown");
    }

    [Fact]
    public void one_dropped_row_is_singular()
    {
        var capped = new CappedReport();
        for (var n = 0; n < 4; n++) capped.Add(n);

        TextGrid.Render(ReportRender.From(capped)).Last()
            .ShouldBe("…and 1 more row not shown");
    }
}

[Collection(ReportVisibilityCollection.Name)]
public class SpecOutputTests
{
    private static List<string> capture(Action body)
    {
        var lines = new List<string>();
        using (SpecOutput.Open(lines.Add)) body();
        return lines;
    }

    [Fact]
    public void with_no_sink_open_writing_is_a_silent_no_op()
    {
        SpecOutput.IsOpen.ShouldBeFalse();
        Should.NotThrow(() => SpecOutput.Write("nobody is listening"));
    }

    [Fact]
    public void a_failing_scenarios_report_reaches_the_runners_output_when_it_closes()
    {
        var lines = capture(() =>
        {
            using var recording = ScenarioRecorder.Begin(
                "Appointments", "a proposal is confirmed", publisher: null, runId: Guid.NewGuid());

            SharedProducer.Report();
            recording.FailureDescription = "the patient was never notified";
        });

        lines.ShouldContain("Message activity");
        lines.ShouldContain(l => l.Contains("MovedToErrorQueue") && l.EndsWith("<-- FAILED"));
    }

    [Fact]
    public void a_passing_scenario_writes_nothing_until_the_run_asks_for_verbose()
    {
        ScenarioReportVisibility.Reset();

        var quiet = capture(() =>
        {
            using var recording = ScenarioRecorder.Begin(
                "Appointments", "a proposal is confirmed", publisher: null, runId: Guid.NewGuid());

            SharedProducer.Report();
        });

        // The scenario itself is written either way; it is the report that waits for verbose.
        quiet.ShouldNotContain("Message activity");

        try
        {
            ScenarioReportVisibility.Verbose = true;

            var verbose = capture(() =>
            {
                using var recording = ScenarioRecorder.Begin(
                    "Appointments", "a proposal is confirmed", publisher: null, runId: Guid.NewGuid());

                SharedProducer.Report();
            });

            verbose.ShouldContain("Message activity");
        }
        finally
        {
            ScenarioReportVisibility.Reset();
        }
    }

    [Fact]
    public void a_closing_scenario_is_written_to_the_output_as_plain_text()
    {
        var lines = capture(() =>
        {
            using var recording = ScenarioRecorder.Begin(
                "Appointments", "a proposal is confirmed", publisher: null, runId: Guid.NewGuid());

            using (ScenarioRecorder.Step("Given", "a proposed appointment")) { }
            using (ScenarioRecorder.Step("Then", "the appointment is confirmed")) { }
        });

        lines.ShouldContain("Feature: Appointments");
        lines.ShouldContain(l => l.Contains("a proposal is confirmed") && l.Contains("OK"));
        lines.ShouldContain(l => l.Contains("✓") && l.Contains("Given") && l.Contains("a proposed appointment"));
        lines.ShouldContain(l => l.Contains("Then") && l.Contains("the appointment is confirmed"));

        // A test pane shows text, never escape codes.
        lines.ShouldAllBe(l => !l.Contains('\u001b'));
    }

    [Fact]
    public void the_scenario_can_be_turned_off_leaving_only_the_reports()
    {
        var previous = Environment.GetEnvironmentVariable(SpecOutput.ScenarioVariable);
        try
        {
            Environment.SetEnvironmentVariable(SpecOutput.ScenarioVariable, "0");

            var lines = capture(() =>
            {
                using var recording = ScenarioRecorder.Begin(
                    "Appointments", "a proposal is confirmed", publisher: null, runId: Guid.NewGuid());

                using (ScenarioRecorder.Step("Given", "a proposed appointment")) { }
            });

            lines.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SpecOutput.ScenarioVariable, previous);
        }
    }

    [Fact]
    public void a_log_line_reaches_the_output_as_it_is_written_rather_than_at_the_end()
    {
        var lines = capture(() =>
        {
            var context = new SpecExecutionContext("spec");

            context.Log("first");
            SpecOutput.Write("the test's own write");
            context.Log("second");
        });

        // Source order, one stream — not Bobcat's lines in a block after the test's own.
        lines.ShouldBe(["first", "the test's own write", "second"]);
    }

    [Fact]
    public void a_sink_that_throws_cannot_fail_the_test()
    {
        // A runner whose output helper has already been torn down throws, and a specification that
        // reported something must not become a failure because the reporting channel closed first.
        using (SpecOutput.Open(_ => throw new InvalidOperationException("there is no active test")))
        {
            Should.NotThrow(() => SpecOutput.Write("anything"));

            Should.NotThrow(() =>
            {
                using var recording = ScenarioRecorder.Begin(
                    "F", "s", publisher: null, runId: Guid.NewGuid());

                SharedProducer.Report();
                recording.FailureDescription = "red";
            });
        }
    }

    [Fact]
    public void the_sink_is_restored_rather_than_cleared_when_a_scope_closes()
    {
        var outer = new List<string>();

        using (SpecOutput.Open(outer.Add))
        {
            var inner = new List<string>();
            using (SpecOutput.Open(inner.Add)) SpecOutput.Write("inner");

            SpecOutput.Write("outer");
            inner.ShouldBe(["inner"]);
        }

        outer.ShouldBe(["outer"]);
        SpecOutput.IsOpen.ShouldBeFalse();
    }
}
