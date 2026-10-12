using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Reporting;

/// <summary>
/// The property <c>WolverineFx.Bobcat</c> depends on (issue #408): a report producer written against
/// the static <see cref="SpecReport"/> surface renders on the projected test it ran inside. (This
/// class once pinned the same producer against the Gherkin lane too; that lane is gone, bobcat#471.)
/// </summary>
[Collection(ReportVisibilityCollection.Name)]
public class BothLanesReportTests
{
    private static SpecRender fromTheProjectedLane(bool failing = true)
    {
        using var recording = ScenarioRecorder.Begin(
            "Appointments", "a proposal is confirmed", publisher: null, runId: Guid.NewGuid());

        SharedProducer.Report();

        if (failing) recording.FailureDescription = "the patient was never notified";

        // Read before disposal: Dispose is what closes the scenario and clears the ambient sink.
        return SpecRender.FromRecording(recording);
    }

    [Fact]
    public void the_producer_renders_its_report_on_the_scenario()
    {
        var projected = fromTheProjectedLane();

        projected.Succeeded.ShouldBeFalse();

        var report = projected.Reports.ShouldHaveSingleItem();
        report.Title.ShouldBe("Message activity");
        report.ShortTitle.ShouldBe("Messages");
    }

    [Fact]
    public void the_grid_carries_the_dead_lettered_row_as_the_only_judged_cell()
    {
        var grid = fromTheProjectedLane().Reports.ShouldHaveSingleItem().Grid;

        grid.Columns.ShouldBe(["at (ms)", "event", "message", "destination", "attempt"]);
        grid.Rows.Count.ShouldBe(4);

        var judged = grid.Rows
            .SelectMany(r => r.Cells)
            .Where(c => c.Status is not (ResultStatus.ok or ResultStatus.success))
            .ToList();

        judged.ShouldHaveSingleItem().DisplayText
            .ShouldBe("expected 'MessageSucceeded', got 'MovedToErrorQueue'");

        // And the informational cells still read as their own text rather than as an empty
        // comparison — the regression CellResult.copy exists to prevent.
        grid.Rows[0].Cells.Single(c => c.Column == "event").DisplayText.ShouldBe("Sent");
        grid.Rows[2].Cells.Single(c => c.Column == "destination").DisplayText.ShouldBe("NULL");
    }

    [Fact]
    public void a_passing_scenario_carries_no_report_until_the_run_asks_for_verbose()
    {
        ScenarioReportVisibility.Reset();
        fromTheProjectedLane(failing: false).Reports.ShouldBeEmpty();

        try
        {
            ScenarioReportVisibility.Verbose = true;
            fromTheProjectedLane(failing: false).Reports.ShouldHaveSingleItem();
        }
        finally
        {
            ScenarioReportVisibility.Reset();
        }
    }
}

public class ReportsOnTheWireTests
{
    private static MessageActivityReport aReport()
    {
        var report = new MessageActivityReport();
        report.Record(0, "Sent", "ConfirmAppointment", "local://confirm", 1);
        report.DeadLettered(104, "NotifyPatient", "rabbitmq://notify", 3);
        return report;
    }

    [Fact]
    public void nothing_to_report_is_null_rather_than_an_empty_list()
    {
        // Null and "a publisher too old to know about reports" have to look the same to a
        // consumer, because they mean the same thing.
        MonitorReports.From([], scenarioFailed: true).ShouldBeNull();
        MonitorReports.From([new MessageActivityReport()], scenarioFailed: true).ShouldBeNull();
    }

    [Fact]
    public void a_passing_scenarios_reports_do_not_reach_the_wire_by_default()
    {
        ScenarioReportVisibility.Reset();
        MonitorReports.From([aReport()], scenarioFailed: false).ShouldBeNull();
        MonitorReports.From([aReport()], scenarioFailed: true).ShouldNotBeNull();
    }

    [Fact]
    public void an_informational_cell_travels_as_a_value_and_a_judged_one_as_its_pair()
    {
        var wire = MonitorReports.From([aReport()], scenarioFailed: true).ShouldNotBeNull()
            .ShouldHaveSingleItem();

        wire.Title.ShouldBe("Message activity");
        wire.ShortTitle.ShouldBe("Messages");
        wire.Columns.ShouldBe(["at (ms)", "event", "message", "destination", "attempt"]);
        wire.SuppressedRows.ShouldBe(0);

        var informational = wire.Cells.First(c => c is { RowIndex: 0, Name: "event" });
        informational.Value.ShouldBe("Sent");
        informational.Expected.ShouldBeNull();
        informational.Actual.ShouldBeNull();

        var judged = wire.Cells.First(c => c is { RowIndex: 1, Name: "event" });
        judged.Expected.ShouldBe("MessageSucceeded");
        judged.Actual.ShouldBe("MovedToErrorQueue");

        // A cell says exactly one thing (issue #396): the judged one fills no Value.
        judged.Value.ShouldBeNull();
    }

    [Fact]
    public void the_suppressed_row_count_travels_so_a_consumer_can_say_the_table_was_cut()
    {
        var capped = new CappedReport();
        for (var n = 0; n < 10; n++) capped.Add(n);

        MonitorReports.From([capped], scenarioFailed: true).ShouldNotBeNull()
            .ShouldHaveSingleItem().SuppressedRows.ShouldBe(7);
    }
}
