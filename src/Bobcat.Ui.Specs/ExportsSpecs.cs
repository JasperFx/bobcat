using Bobcat.Xunit;

namespace Bobcat.Ui.Specs;

/// <summary>
/// The eject formats are rendered from the same projection the dashboard reads, so what CI sees
/// in a report is what the viewer showed: CTRF first-class, JUnit as the compatibility floor,
/// NDJSON as the raw archive.
/// </summary>
[BobcatFeature("Exports"), BobcatScenario]
public class ExportsSpecs(MonitorHost host) : ViewerSpec(host)
{
    /// <summary>
    /// The feature's Background: one finished run with a pass, a failure and a pass on retry. Called first
    /// in each test rather than from <c>InitializeAsync</c>, so its steps open inside the recording and render.
    /// </summary>
    private async Task givenAFinishedOrdersRun()
    {
        await GivenARunHasStartedWith("Orders", 3);
        await GivenTheseScenariosHaveFinished("""
            | Uid                       | Outcome     | Attempts |
            | Orders/place an order     | CleanPass   | 1        |
            | Orders/cancel an order    | Failed      | 1        |
            | Orders/talk to the broker | PassOnRetry | 2        |
            """);
        await GivenTheRunHasFinished(1);
    }

    [Fact]
    public async Task The_CTRF_report_reflects_the_ingested_outcomes()
    {
        await givenAFinishedOrdersRun();
        await WhenTheRunIsExportedAs("ctrf");
        ThenTheExportRespondsWith(200);
        ThenTheExportIsServedAs("application/json");
        ThenTheCtrfSummaryCounts(2, 1, 1);
        ThenTheCtrfTestHasRetries("Orders: talk to the broker", 1);
        ThenTheCtrfTestIsFlaky("Orders: talk to the broker");
        ThenTheCtrfStatusIs("Orders: cancel an order", "failed");
        ThenTheCtrfStatusIs("Orders: place an order", "passed");
    }

    [Fact]
    public async Task The_JUnit_report_is_the_compatibility_floor()
    {
        await givenAFinishedOrdersRun();
        await WhenTheRunIsExportedAs("junit");
        ThenTheExportRespondsWith(200);
        ThenTheExportIsServedAs("application/xml");
        ThenTheJUnitReportCounts(3, 1);
    }

    [Fact]
    public async Task The_NDJSON_archive_is_the_raw_event_stream()
    {
        await givenAFinishedOrdersRun();
        await WhenTheRunIsExportedAs("ndjson");
        ThenTheExportRespondsWith(200);
        ThenTheExportIsServedAs("application/x-ndjson");
        ThenTheNdjsonExportHas(8);
    }

    [Fact]
    public async Task An_unknown_format_is_refused()
    {
        await givenAFinishedOrdersRun();
        await WhenTheRunIsExportedAs("yaml");
        ThenTheExportRespondsWith(400);
    }
}
