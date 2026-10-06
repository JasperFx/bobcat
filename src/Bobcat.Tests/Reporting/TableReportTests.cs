using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.Reporting;

public class TableReportTests
{
    [Fact]
    public void columns_are_in_first_seen_order_and_rows_are_stamped()
    {
        var report = new MessageActivityReport();
        report.Record(0, "Sent", "ConfirmAppointment", "local://confirm", 1);
        report.Record(3, "Received", "ConfirmAppointment", "local://confirm", 1);

        report.Columns.ShouldBe(["at (ms)", "event", "message", "destination", "attempt"]);
        report.Cells.Where(c => c.RowIndex == 0).Select(c => c.Name)
            .ShouldBe(["at (ms)", "event", "message", "destination", "attempt"]);
        report.Cells.Count(c => c.RowIndex == 1).ShouldBe(5);
        report.SuppressedRows.ShouldBe(0);
    }

    [Fact]
    public void a_plain_row_judges_nothing()
    {
        var report = new MessageActivityReport();
        report.Record(19, "MessageSucceeded", "ConfirmAppointment", null, 1);

        foreach (var cell in report.Cells)
        {
            // The whole honesty claim of a report: every cell is #396's unjudged VALUE shape, so
            // nothing in the table can read as a comparison that was made and passed.
            cell.Expected.ShouldBeNull();
            cell.Actual.ShouldBeNull();
            cell.Status.ShouldBe(ResultStatus.ok);
        }
    }

    [Fact]
    public void a_null_value_renders_as_the_same_token_every_other_cell_uses()
    {
        var report = new MessageActivityReport();
        report.Record(19, "MessageSucceeded", "ConfirmAppointment", null, 1);

        report.Cells.Single(c => c.Name == "destination").DisplayText.ShouldBe("NULL");
    }

    [Fact]
    public void one_row_may_judge_while_the_rest_of_the_table_does_not()
    {
        var report = new MessageActivityReport();
        report.Record(0, "Sent", "NotifyPatient", "rabbitmq://notify", 1);
        report.DeadLettered(104, "NotifyPatient", "rabbitmq://notify", 3);

        var judged = report.Cells.Single(c => c.Status == ResultStatus.failed);
        judged.RowIndex.ShouldBe(1);
        judged.Name.ShouldBe("event");
        judged.DisplayText.ShouldBe("expected 'MessageSucceeded', got 'MovedToErrorQueue'");

        // And the row it sits in keeps its informational cells, so the red cell has context.
        report.Cells.Count(c => c.RowIndex == 1).ShouldBe(5);
    }

    [Fact]
    public void rows_past_the_cap_are_counted_rather_than_dropped_silently()
    {
        var report = new CappedReport();
        for (var n = 0; n < 10; n++) report.Add(n);

        report.RowCount.ShouldBe(3);
        report.SuppressedRows.ShouldBe(7);
        report.Cells.Count.ShouldBe(3);
    }

    [Fact]
    public void a_caller_supplied_row_index_is_restamped_to_the_real_row()
    {
        var report = new CappedReport();
        report.Add(1);
        report.Add(2);

        // The producer never has to know its own row number — CellResult.RowIndex defaults to -1,
        // and the base class is what makes the grid reassemblable.
        report.Cells.Select(c => c.RowIndex).ShouldBe([0, 1]);
    }
}

public class ScenarioReportVisibilityTests
{
    [Theory]
    // visibility,                     failed, verbose, written
    [InlineData(ReportVisibility.OnFailure, false, false, false)]
    [InlineData(ReportVisibility.OnFailure, true, false, true)]
    [InlineData(ReportVisibility.OnFailure, false, true, true)]
    [InlineData(ReportVisibility.Always, false, false, true)]
    [InlineData(ReportVisibility.Always, true, false, true)]
    public void the_latch(ReportVisibility visibility, bool failed, bool verbose, bool written)
    {
        IScenarioReport report = visibility == ReportVisibility.Always
            ? new AlwaysReport()
            : new MessageActivityReport();

        ScenarioReportVisibility.ShouldWrite(report, failed, verbose).ShouldBe(written);
    }

    [Fact]
    public void an_empty_report_is_dropped_whatever_its_visibility()
    {
        // A grammar that was never exercised legitimately produces one, and an empty grid under a
        // heading says less than no heading at all.
        var always = new AlwaysReport();

        ScenarioReportVisibility.Filter([always], scenarioFailed: true, verbose: true).ShouldBeEmpty();

        always.Add("something");
        ScenarioReportVisibility.Filter([always], scenarioFailed: false, verbose: false).ShouldHaveSingleItem();
    }
}

public class ReportSinkTests
{
    [Fact]
    public void report_for_is_get_or_create_one_per_type()
    {
        var results = new ExecutionResults("spec", DateTimeOffset.UtcNow);

        var first = results.ReportFor<MessageActivityReport>();
        first.Record(0, "Sent", "X", null, 1);

        var second = results.ReportFor<MessageActivityReport>();

        // The cross-grammar contract: two grammars agreeing only on the type append to one table.
        second.ShouldBeSameAs(first);
        results.Reports.ShouldHaveSingleItem();
    }

    [Fact]
    public void attach_replaces_a_report_of_the_same_type_rather_than_adding_a_second()
    {
        var results = new ExecutionResults("spec", DateTimeOffset.UtcNow);
        results.ReportFor<MessageActivityReport>();

        var replacement = new MessageActivityReport();
        replacement.Record(0, "Sent", "X", null, 1);
        results.AttachReport(replacement);

        results.Reports.ShouldHaveSingleItem().ShouldBeSameAs(replacement);
    }

    [Fact]
    public void the_static_facade_reaches_the_ambient_scenario()
    {
        var results = new ExecutionResults("spec", DateTimeOffset.UtcNow);

        using (ScenarioReports.Open(results))
        {
            SpecReport.IsRecording.ShouldBeTrue();
            SharedProducer.Report();
        }

        results.Reports.ShouldHaveSingleItem().ShouldBeOfType<MessageActivityReport>()
            .Cells.ShouldNotBeEmpty();
    }

    [Fact]
    public void outside_a_scenario_it_reports_to_nobody_rather_than_throwing()
    {
        ScenarioReports.Current.ShouldBeNull();
        SpecReport.IsRecording.ShouldBeFalse();

        // A decorated grammar helper gets called from plenty of places that are not
        // specifications; reporting from them is noise at best and must never be a failure.
        Should.NotThrow(SharedProducer.Report);
        Should.NotThrow(() => SpecReport.Attach(new AlwaysReport()));
    }

    [Fact]
    public void a_nested_scope_restores_the_scenario_it_interrupted()
    {
        var outer = new ExecutionResults("outer", DateTimeOffset.UtcNow);
        var inner = new ExecutionResults("inner", DateTimeOffset.UtcNow);

        using (ScenarioReports.Open(outer))
        {
            using (ScenarioReports.Open(inner))
            {
                ScenarioReports.Current.ShouldBeSameAs(inner);
            }

            // Restores rather than clears: a feature-level bracket around a scenario-level one must
            // not leave the outer scenario unable to report.
            ScenarioReports.Current.ShouldBeSameAs(outer);
        }

        ScenarioReports.Current.ShouldBeNull();
    }
}
