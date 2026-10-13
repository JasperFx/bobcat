using Bobcat.Xunit;

namespace Bobcat.Ui.Specs;

/// <summary>
/// A retried scenario keeps its history -- the attempts that were watched and the verdict that
/// followed -- and a pass on retry is never folded into a clean pass.
/// </summary>
[BobcatFeature("Retries"), BobcatScenario]
public class RetriesSpecs(MonitorHost host) : ViewerSpec(host)
{
    [Fact]
    public async Task A_retried_scenario_shows_its_attempts()
    {
        await GivenARunHasStartedWith("Orders", 1);
        await GivenTheScenarioHasStarted("Orders/talk to the broker");
        await GivenTheseStepsRan("""
            | Kind | Text                | Status |
            | When | the broker is asked | error  |
            """);
        await GivenARetryWasScheduled("Orders/talk to the broker", 2, "the broker is slow to warm up");
        await GivenTheScenarioHasStarted("Orders/talk to the broker");
        await GivenTheseStepsRan("""
            | Kind | Text                | Status  |
            | When | the broker is asked | success |
            """);
        await GivenTheScenarioFinishedAfter("Orders/talk to the broker", "PassOnRetry", 2);
        await ThenTheScenarioShowsAttempts("Orders/talk to the broker", 2);
        await ThenTheOutcomeIs("Orders/talk to the broker", "PassOnRetry");
        await ThenTheRetryReasonsInclude("Orders/talk to the broker", "the broker is slow to warm up");
        await ThenTheFinalAttemptRan("Orders/talk to the broker", 1);
    }

    [Fact]
    public async Task A_fresh_process_retry_that_counts_from_one_is_still_the_second_attempt()
    {
        await GivenARunHasStartedWith("Orders", 1);
        await GivenTheScenarioHasStarted("Orders/talk to the broker");
        await GivenARetryWasScheduled("Orders/talk to the broker", 2, "flaky broker");
        await GivenTheScenarioStartedItsAttempt("Orders/talk to the broker", 1);
        await GivenTheScenarioFinishedAfter("Orders/talk to the broker", "PassOnRetry", 1);
        await ThenTheScenarioShowsAttempts("Orders/talk to the broker", 2);
    }

    [Fact]
    public async Task A_pass_on_retry_is_reported_apart_from_a_clean_pass()
    {
        await GivenARunHasStartedWith("Orders", 2);
        await GivenTheseScenariosHaveFinished("""
            | Uid                       | Outcome     | Attempts |
            | Orders/place an order     | CleanPass   | 1        |
            | Orders/talk to the broker | PassOnRetry | 2        |
            """);
        await WhenTheRunFinishes(0);
        await ThenTheRunsSummaryReportsTheVerdict(1, 0, 1);
        await ThenTheRunsExitCodeIs(0);
    }
}
