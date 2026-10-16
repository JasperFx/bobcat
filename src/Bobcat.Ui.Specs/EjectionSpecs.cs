using Bobcat.Xunit;

namespace Bobcat.Ui.Specs;

/// <summary>
/// Ejecting a run takes it off the dashboard but never deletes its archive, and what the viewer
/// knows is rebuilt from the archives when it restarts -- so an eject survives a restart, and so
/// does everything that was not ejected.
/// </summary>
[BobcatFeature("Ejection"), BobcatScenario]
public class EjectionSpecs(MonitorHost host) : ViewerSpec(host)
{
    [Fact]
    public async Task Ejecting_a_run_removes_its_card()
    {
        await GivenARunHasStartedWith("Orders", 1);
        await GivenTheScenarioHasStarted("Orders/place an order");
        await GivenTheScenarioFinishedAs("Orders/place an order", "CleanPass");
        ThenTheRunsArchiveIsOnDisk();
        await WhenTheRunIsEjected();
        ThenTheEjectRespondsWith(204);
        await ThenTheRunIsNotInTheRunList("Orders");
        await ThenAskingForTheRunRespondsWith(404);
        ThenTheRunsArchiveHasMovedToTheEjectedFolder();
    }

    [Fact]
    public async Task Ejecting_an_unknown_run_is_a_404()
    {
        await WhenAnUnknownRunIsEjected();
        ThenTheEjectRespondsWith(404);
    }

    [Fact]
    public async Task A_live_runs_archive_survives_a_restart()
    {
        await GivenARunHasStartedWith("Orders", 2);
        await GivenTheScenarioHasStarted("Orders/place an order");
        await GivenTheScenarioFinishedAs("Orders/place an order", "CleanPass");
        await WhenTheViewerRestarts();
        await ThenTheRunAppears("Orders");
        await ThenTheRunIsListedAs("orphaned");
        await ThenTheRunsSummaryReportsProgress(1, 2);
        await ThenTheOutcomeIs("Orders/place an order", "CleanPass");
    }

    [Fact]
    public async Task A_finished_run_rehydrates_as_finished_not_orphaned()
    {
        await GivenARunHasStartedWith("Orders", 1);
        await GivenTheScenarioHasStarted("Orders/place an order");
        await GivenTheScenarioFinishedAs("Orders/place an order", "CleanPass");
        await GivenTheRunHasFinished(0);
        await WhenTheViewerRestarts();
        await ThenTheRunIsListedAs("finished");
        await ThenTheRunsSummaryReportsTheVerdict(1, 0, 0);
    }

    [Fact]
    public async Task An_eject_survives_a_restart()
    {
        await GivenARunHasStartedWith("Orders", 1);
        await GivenTheScenarioHasStarted("Orders/place an order");
        await WhenTheRunIsEjected();
        await WhenTheViewerRestarts();
        await ThenTheRunIsNotInTheRunList("Orders");
        ThenTheRunsArchiveHasMovedToTheEjectedFolder();
    }

    [Fact]
    public async Task Ejecting_the_whole_board_takes_every_run_at_once()
    {
        await GivenARunHasStartedWith("Orders", 1);
        await GivenTheScenarioFinishedAs("Orders/place an order", "CleanPass");
        await GivenTheRunHasFinished(0);
        await GivenARunHasStartedWith("Payments", 1);
        await GivenTheScenarioFinishedAs("Payments/charge a card", "CleanPass");
        await GivenTheRunHasFinished(0);
        await WhenEveryRunIsEjected();
        ThenTheBulkEjectReports(2);
        await ThenTheRunIsNotInTheRunList("Orders");
        await ThenTheRunIsNotInTheRunList("Payments");
    }

    [Fact]
    public async Task Ejecting_all_older_spares_the_run_it_was_anchored_on()
    {
        await GivenARunStartedMinutesAgo("Yesterday", 600);
        await GivenTheRunHasFinished(0);
        await GivenARunStartedMinutesAgo("Anchor", 30);
        await GivenTheRunHasFinished(0);
        await GivenARunHasStarted("Newest");
        await GivenTheRunHasFinished(0);
        await WhenEveryRunOlderThanIsEjected("Anchor");
        ThenTheBulkEjectReports(1);
        await ThenTheRunIsNotInTheRunList("Yesterday");
        await ThenTheRunAppears("Anchor");
        await ThenTheRunAppears("Newest");
    }

    [Fact]
    public async Task Ejecting_all_but_one_keeps_exactly_that_one()
    {
        await GivenARunHasStarted("Keep");
        await GivenTheRunHasFinished(0);
        await GivenARunHasStarted("Drop");
        await GivenTheRunHasFinished(0);
        await WhenEveryRunButIsEjected("Keep");
        ThenTheBulkEjectReports(1);
        await ThenTheRunAppears("Keep");
        await ThenTheRunIsNotInTheRunList("Drop");
    }

    // stoat#83: a board filtered to one repository ejects what it shows. The slug is sent upper-cased,
    // because case never splits a repository.
    [Fact]
    public async Task Ejecting_all_within_one_repository_leaves_every_other_run_on_the_board()
    {
        await GivenARunInThisCheckoutHasStarted("Ours");
        await GivenTheRunHasFinished(0);
        await GivenARunHasStarted("Elsewhere");
        await GivenTheRunHasFinished(0);
        await WhenEveryRunInThisRepositoryIsEjected();
        ThenTheBulkEjectReports(1);
        await ThenTheRunIsNotInTheRunList("Ours");
        await ThenTheRunAppears("Elsewhere");
    }

    [Fact]
    public async Task Ejecting_the_unattributed_runs_leaves_every_run_with_a_repository()
    {
        await GivenARunInThisCheckoutHasStarted("Ours");
        await GivenTheRunHasFinished(0);
        await GivenARunHasStarted("Elsewhere");
        await GivenTheRunHasFinished(0);
        await WhenEveryUnattributedRunIsEjected();
        ThenTheBulkEjectReports(1);
        await ThenTheRunIsNotInTheRunList("Elsewhere");
        await ThenTheRunAppears("Ours");
    }

    // A live publisher recreates the entry with its next event, so ejecting a live run would buy
    // a card that reappears and a count that lied. The bulk verbs leave it where it is.
    [Fact]
    public async Task A_bulk_eject_never_takes_a_run_that_is_still_live()
    {
        await GivenARunHasStarted("Finished");
        await GivenTheRunHasFinished(0);
        await GivenARunHasStartedWith("StillRunning", 10);
        await WhenEveryRunIsEjected();
        ThenTheBulkEjectReports(1);
        await ThenTheRunAppears("StillRunning");
        await ThenTheRunIsNotInTheRunList("Finished");
    }
}
