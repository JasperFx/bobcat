using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Reporting;

/// <summary>
/// The property <c>WolverineFx.Bobcat</c> depends on (issue #408): one report producer, written
/// against the static <see cref="SpecReport"/> surface with no knowledge of which lane it is in,
/// renders identically from a Gherkin scenario and from a projected test.
/// </summary>
/// <remarks>
/// <b>Both lanes from one class, deliberately</b> — the same reasoning as
/// <c>SpecIdentityEndToEndTests</c> and <c>MarkerSpecNamingAgreementTests</c>. Split across two
/// test classes, one lane gains a rule the other never hears about, and a cross-lane grammar is
/// exactly the thing that would then break in only one of them.
/// </remarks>
[Collection(ReportVisibilityCollection.Name)]
public class BothLanesReportTests
{
    private static SpecRender fromTheGherkinLane()
    {
        var results = new ExecutionResults("Appointments/a proposal is confirmed", DateTimeOffset.UtcNow);

        // A wrong, so the OnFailure default is satisfied without needing verbose.
        results.Counts.Read(ResultStatus.failed);

        using (ScenarioReports.Open(results))
        {
            SharedProducer.Report();
        }

        return SpecRender.FromResults("a proposal is confirmed", results, "Appointments");
    }

    private static SpecRender fromTheProjectedLane()
    {
        using var recording = ScenarioRecorder.Begin(
            "Appointments", "a proposal is confirmed", publisher: null, runId: Guid.NewGuid());

        SharedProducer.Report();

        recording.FailureDescription = "the patient was never notified";

        // Read before disposal: Dispose is what closes the scenario and clears the ambient sink.
        return SpecRender.FromRecording(recording);
    }

    [Fact]
    public void the_same_producer_renders_the_same_grid_in_both_lanes()
    {
        var gherkin = fromTheGherkinLane();
        var projected = fromTheProjectedLane();

        gherkin.Succeeded.ShouldBeFalse();
        projected.Succeeded.ShouldBeFalse();

        var fromFeature = gherkin.Reports.ShouldHaveSingleItem();
        var fromTest = projected.Reports.ShouldHaveSingleItem();

        fromFeature.Title.ShouldBe("Message activity");
        fromTest.Title.ShouldBe(fromFeature.Title);
        fromTest.ShortTitle.ShouldBe(fromFeature.ShortTitle);
        fromTest.Grid.Columns.ShouldBe(fromFeature.Grid.Columns);
        fromTest.Grid.Rows.Count.ShouldBe(fromFeature.Grid.Rows.Count);

        for (var row = 0; row < fromFeature.Grid.Rows.Count; row++)
        {
            fromTest.Grid.Rows[row].Cells.Select(c => $"{c.Column}={c.DisplayText}")
                .ShouldBe(fromFeature.Grid.Rows[row].Cells.Select(c => $"{c.Column}={c.DisplayText}"));
        }
    }

    [Fact]
    public void the_grid_carries_the_dead_lettered_row_as_the_only_judged_cell()
    {
        var grid = fromTheGherkinLane().Reports.ShouldHaveSingleItem().Grid;

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
        var results = new ExecutionResults("spec", DateTimeOffset.UtcNow);
        results.Counts.Read(ResultStatus.success);

        using (ScenarioReports.Open(results)) SharedProducer.Report();

        ScenarioReportVisibility.Reset();
        SpecRender.FromResults("spec", results).Reports.ShouldBeEmpty();

        try
        {
            ScenarioReportVisibility.Verbose = true;
            SpecRender.FromResults("spec", results).Reports.ShouldHaveSingleItem();
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
