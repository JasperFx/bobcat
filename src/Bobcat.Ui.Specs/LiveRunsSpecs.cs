using Bobcat.Xunit;

namespace Bobcat.Ui.Specs;

/// <summary>
/// The dashboard is fed by POST /api/ingest and read back over GET /api/runs -- the same public
/// wire an outside consumer uses, so nothing here needs a browser.
/// </summary>
[BobcatFeature("Live Runs"), BobcatScenario]
public class LiveRunsSpecs(MonitorHost host) : ViewerSpec(host)
{
    [Fact]
    public async Task A_run_appears_when_it_starts()
    {
        await GivenARunHasStartedWith("Orders", 3);
        await ThenTheRunAppears("Orders");
        await ThenTheRunIsListedAs("running");
        await ThenTheRunsSummaryReportsProgress(0, 3);
    }

    [Fact]
    public async Task Progress_is_visible_while_the_run_is_still_going()
    {
        await GivenARunHasStartedWith("Orders", 3);
        await GivenTheScenarioHasStarted("Orders/place an order");
        await GivenTheScenarioFinishedAs("Orders/place an order", "CleanPass");
        await ThenTheRunsSummaryReportsProgress(1, 3);
        await ThenTheRunIsListedAs("running");
    }

    [Fact]
    public async Task A_finished_run_carries_its_verdict()
    {
        await GivenARunHasStartedWith("Orders", 2);
        await GivenTheseScenariosHaveFinished("""
            | Uid                    | Outcome   | Attempts |
            | Orders/place an order  | CleanPass | 1        |
            | Orders/cancel an order | Failed    | 1        |
            """);
        await WhenTheRunFinishes(1);
        await ThenTheRunIsListedAs("finished");
        await ThenTheRunsSummaryReportsTheVerdict(1, 1, 0);
        await ThenTheRunsExitCodeIs(1);
    }

    [Fact]
    public async Task A_scenarios_run_evidence_is_readable_per_scenario()
    {
        await GivenARunHasStartedWith("Wallets", 1);
        await GivenTheScenarioHasStarted("Credit Wallet/happy path");
        await GivenTheScenarioFinishedTouching("Credit Wallet/happy path", "CreditWallet, WalletCredited, WalletSummary");
        await ThenTheTouchedTypesAre("Credit Wallet/happy path", "CreditWallet, WalletCredited, WalletSummary");
        await ThenTheEvidenceIsStamped("Credit Wallet/happy path");
    }

    [Fact]
    public async Task Runs_are_found_by_their_correlation_tag()
    {
        await GivenATaggedRunHasStarted("Orders", "build-42");
        await GivenATaggedRunHasStarted("Payments", "build-43");
        await ThenTheRunListFilteredByTagShowsOnly("build-42", "Orders");
        await ThenTheRunAppears("Payments");
    }

    // Found by walking every route with an empty body: it used to answer 500.
    [Fact]
    public async Task A_batch_with_no_events_is_the_senders_error_not_the_viewers()
    {
        await WhenABatchWithNoEventsIsIngested();
        ThenTheIngestionRespondsWith(400);
    }
}
