using Bobcat.Xunit;

namespace Bobcat.Ui.Specs;

/// <summary>
/// Issue #195: a supervised suite whose workers are not Bobcat runners publishes no scenarios of
/// its own -- a plain xUnit worker has no MonitorPublishingObserver. The supervisor forwards its
/// live per-test stream instead, and that is what moves the run card. Before it, a card
/// registered with the right total and sat at 0 finished for the whole run: the shape of a
/// wedged run, from a suite that was working perfectly.
/// </summary>
[BobcatFeature("Foreign Runs"), BobcatScenario]
public class ForeignRunsSpecs(MonitorHost host) : ViewerSpec(host)
{
    [Fact]
    public async Task A_forwarded_verdict_moves_the_runs_progress()
    {
        await GivenARunHasStartedWith("ServiceTests", 3);
        await GivenAForeignTestHasStarted("Acme.OrderTests.pays");
        await GivenAForeignTestFinished("Acme.OrderTests.pays", "Passed");
        await GivenAForeignTestFinished("Acme.OrderTests.refunds", "Failed");
        await ThenTheRunsSummaryReportsProgress(2, 3);
        await ThenTheOutcomeIs("Acme.OrderTests.pays", "CleanPass");
        await ThenTheOutcomeIs("Acme.OrderTests.refunds", "Failed");
    }

    // A test in flight is known, so a lane can say what it is working through -- but it is not done.
    [Fact]
    public async Task A_test_still_in_flight_is_not_counted_as_finished()
    {
        await GivenARunHasStartedWith("ServiceTests", 2);
        await GivenAForeignTestHasStarted("Acme.OrderTests.pays");
        await ThenTheRunsSummaryReportsProgress(0, 2);
    }

    // Skipped counts as a pass because the supervisor's own WorkerOutcome.Succeeded does -- so the
    // progress bar and the terminal counts cannot disagree about the same test.
    [Fact]
    public async Task A_skipped_test_is_a_pass_on_the_same_terms_the_runs_own_counts_use()
    {
        await GivenARunHasStartedWith("ServiceTests", 1);
        await GivenAForeignTestFinished("Acme.OrderTests.ignored", "Skipped");
        await ThenTheRunsSummaryReportsProgress(1, 1);
        await ThenTheOutcomeIs("Acme.OrderTests.ignored", "CleanPass");
    }

    // A supervised Bobcat suite puts BOTH streams on the wire; the worker's own is richer and
    // owns the uid, whichever arrives first.
    [Fact]
    public async Task A_worker_publishing_its_own_scenario_is_not_double_reported()
    {
        await GivenARunHasStartedWith("Specs", 1);
        await GivenTheScenarioHasStarted("Orders/place an order");
        await GivenTheScenarioFinishedAfter("Orders/place an order", "PassOnRetry", 2);
        await GivenAForeignTestFinished("Orders/place an order", "Passed");
        await ThenTheRunsSummaryReportsProgress(1, 1);
        await ThenTheOutcomeIs("Orders/place an order", "PassOnRetry");
        await ThenTheScenarioShowsAttempts("Orders/place an order", 2);
    }

    // Free consequence: a supervised xUnit run now has per-test rows, so it ejects as CTRF like
    // any other -- from a suite with no Bobcat reference anywhere in it.
    [Fact]
    public async Task A_foreign_run_ejects_as_CTRF()
    {
        await GivenARunHasStartedWith("ServiceTests", 2);
        await GivenAForeignTestFinished("Acme.OrderTests.pays", "Passed");
        await GivenAForeignTestFinished("Acme.OrderTests.refunds", "Failed");
        await GivenTheRunHasFinished(1);
        await WhenTheRunIsExportedAs("ctrf");
        ThenTheExportRespondsWith(200);
        ThenTheCtrfSummaryCounts(1, 1, 0);
        ThenTheCtrfStatusIs("Acme.OrderTests.pays", "passed");
    }
}
