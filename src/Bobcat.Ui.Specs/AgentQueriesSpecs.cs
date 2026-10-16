using Bobcat.Xunit;

namespace Bobcat.Ui.Specs;

/// <summary>
/// bobcat#524 — the reads Stoat served as MCP tools, served over HTTP for the <c>bobcat</c> CLI's
/// query commands. Each defaults to the most recent run, and a refusal is a 400 carrying
/// <c>{ "error": … }</c>, so a shell caller can tell the two apart by exit code alone.
/// </summary>
[BobcatFeature("Agent queries"), BobcatScenario]
public class AgentQueriesSpecs(MonitorHost host) : ViewerSpec(host)
{
    private async Task givenAFinishedOrdersRun()
    {
        await GivenARunHasStartedWith("Orders", 2);
        await GivenTheseScenariosHaveFinished("""
            | Uid                    | Outcome   | Attempts |
            | Orders/place an order  | CleanPass | 1        |
            | Orders/cancel an order | Failed    | 1        |
            """);
        await GivenTheRunHasFinished(1);
    }

    [Fact]
    public async Task Failing_tests_reads_the_most_recent_run()
    {
        await givenAFinishedOrdersRun();
        await WhenAnAgentAsksFor("failing-tests");
        ThenTheAgentsAnswerRespondsWith(200);
        ThenTheAgentsAnswerNamesAsFailing("Orders/cancel an order");
    }

    [Fact]
    public async Task Failing_spec_opens_the_first_failure()
    {
        await givenAFinishedOrdersRun();
        await WhenAnAgentAsksFor("failing-spec");
        ThenTheAgentsAnswerRespondsWith(200);
        ThenTheAgentsAnswerIsTheScenario("Orders/cancel an order", "Failed");
    }

    [Fact]
    public async Task A_refusal_is_a_400_that_says_why()
    {
        await givenAFinishedOrdersRun();
        await WhenAnAgentAsksFor("failing-tests?runId=yesterday");
        ThenTheAgentsAnswerRespondsWith(400);
        ThenTheAgentsAnswerIsTheError("'yesterday' is not a run id");
    }

    [Fact]
    public async Task The_JUnit_export_needs_no_run_id()
    {
        await givenAFinishedOrdersRun();
        await WhenAnAgentAsksFor("export?format=junit");
        ThenTheExportRespondsWith(200);
        ThenTheExportIsServedAs("application/xml");
        ThenTheJUnitReportCounts(2, 1);
    }
}
