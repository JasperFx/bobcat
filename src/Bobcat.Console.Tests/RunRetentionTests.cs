using Bobcat.Console.Contracts;
using Bobcat.Console.Runs;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issues #197 and #198: clearing the board deliberately, and the policy that means you rarely
/// have to. Both are ejection — the NDJSON archive stays on disk under the separate age policy
/// (<see cref="ArchiveRetentionTests"/>) — which is what makes an automatic eviction, and a
/// one-click "take all 43", reasonable controls at all.
/// </summary>
public class RunRetentionTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-runs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static MonitorEvent[] run(Guid runId, string suite, DateTimeOffset startedAt, string repo = "/repo") =>
    [
        new RunStarted(runId, suite, repo, "main", "in-process", startedAt, 1),
        new ScenarioFinished(runId, "Calc/adds", "CleanPass", 1, 20, null),
        new RunFinished(runId, 0, 1, 0, 0, 0, startedAt.AddMinutes(1))
    ];

    private static MonitorEvent[] liveRun(Guid runId, string suite, DateTimeOffset startedAt) =>
    [
        new RunStarted(runId, suite, "/repo", "main", "supervised", startedAt, 100)
    ];

    private static DateTimeOffset at(int minute) => new(2026, 9, 1, 10, minute, 0, TimeSpan.Zero);

    /// <summary>Retention disabled, so a test writes the board it means to test.</summary>
    private MonitorRunRegistry registry(int retainedRuns = 0)
        => new(_dataPath, TimeSpan.Zero, retainedRuns);

    // ---- #197: bulk eject -------------------------------------------------------------

    [Fact]
    public void eject_all_takes_every_finished_run_and_leaves_the_archives_on_disk()
    {
        using var monitor = registry();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var (id, index) in ids.Select((id, i) => (id, i))) monitor.Record(run(id, "Demo", at(index)));

        monitor.RemoveWhere(_ => true).Count.ShouldBe(3);
        monitor.All().ShouldBeEmpty();

        foreach (var id in ids)
        {
            File.Exists(monitor.ArchiveFileFor(id)).ShouldBeFalse();
            File.Exists(monitor.EjectedFileFor(id)).ShouldBeTrue("eject is not delete — the archive moves, it does not go");
        }
    }

    [Fact]
    public void a_live_run_is_never_taken_by_a_bulk_eject_however_broad_the_filter()
    {
        // Not caution — it does not work: the publisher's next event recreates the entry, so
        // ejecting a live run buys a card that reappears and a count that lied.
        using var monitor = registry();
        var live = Guid.NewGuid();
        var done = Guid.NewGuid();
        monitor.Record(liveRun(live, "Gate", at(0)));
        monitor.Record(run(done, "Demo", at(1)));

        monitor.RemoveWhere(_ => true).ShouldBe([done]);
        monitor.All().ShouldHaveSingleItem().RunId.ShouldBe(live);
    }

    [Fact]
    public void an_orphaned_run_is_fair_game_because_its_publisher_is_gone_by_definition()
    {
        var orphan = Guid.NewGuid();
        using (var first = new MonitorRunRegistry(_dataPath, TimeSpan.Zero))
        {
            first.Record(liveRun(orphan, "Gate", at(0)));
        }

        // A restart is what declares it an orphan: an archive with no terminal event.
        using var monitor = registry();
        monitor.Find(orphan)!.Orphaned.ShouldBeTrue();
        monitor.RemoveWhere(_ => true).ShouldBe([orphan]);
    }

    [Fact]
    public void eject_all_older_spares_the_run_it_was_anchored_on()
    {
        using var monitor = registry();
        var older = Guid.NewGuid();
        var anchor = Guid.NewGuid();
        var newer = Guid.NewGuid();
        monitor.Record(run(older, "Demo", at(0)));
        monitor.Record(run(anchor, "Demo", at(5)));
        monitor.Record(run(newer, "Demo", at(9)));

        var cutoff = at(5);
        monitor.RemoveWhere(r => (r.StartedAt ?? DateTimeOffset.MinValue) < cutoff).ShouldBe([older]);
        monitor.All().Select(r => r.RunId).OrderBy(id => id).ShouldBe([anchor, newer], ignoreOrder: true);
    }

    [Fact]
    public void eject_all_but_this_keeps_exactly_one()
    {
        using var monitor = registry();
        var keep = Guid.NewGuid();
        monitor.Record(run(keep, "Demo", at(0)));
        monitor.Record(run(Guid.NewGuid(), "Demo", at(1)));
        monitor.Record(run(Guid.NewGuid(), "Demo", at(2)));

        monitor.RemoveWhere(r => r.RunId != keep).Count.ShouldBe(2);
        monitor.All().ShouldHaveSingleItem().RunId.ShouldBe(keep);
    }

    // ---- #198: bounded run count ------------------------------------------------------

    [Fact]
    public void the_board_keeps_the_most_recent_runs_of_a_suite_and_ejects_the_rest()
    {
        using var monitor = registry(retainedRuns: 2);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var (id, index) in ids.Select((id, i) => (id, i))) monitor.Record(run(id, "Demo", at(index)));

        // The sweep runs inline when a run finishes, so the board never grows past its cap.
        monitor.All().Select(r => r.RunId).ShouldBe([ids[3], ids[4]], ignoreOrder: true);

        // Ejected, not deleted: the archives are all still there under the age policy.
        foreach (var id in ids[..3]) File.Exists(monitor.EjectedFileFor(id)).ShouldBeTrue();
    }

    [Fact]
    public void the_cap_is_per_job_so_a_busy_suite_cannot_starve_a_quiet_one()
    {
        // A global cap on a shared console — today's board carried four repositories at once —
        // would let the busiest of them evict every card the quiet ones had.
        using var monitor = registry(retainedRuns: 2);
        var quiet = Guid.NewGuid();
        monitor.Record(run(quiet, "QuietSuite", at(0), repo: "/other"));
        foreach (var index in Enumerable.Range(1, 5))
        {
            monitor.Record(run(Guid.NewGuid(), "BusySuite", at(index)));
        }

        monitor.All().Count(r => r.Suite == "BusySuite").ShouldBe(2);
        monitor.All().ShouldContain(r => r.RunId == quiet);
    }

    [Fact]
    public void the_same_suite_in_two_repositories_is_two_jobs()
    {
        using var monitor = registry(retainedRuns: 1);
        var here = Guid.NewGuid();
        var worktree = Guid.NewGuid();
        monitor.Record(run(here, "Demo", at(0), repo: "/repo"));
        monitor.Record(run(worktree, "Demo", at(1), repo: "/repo-worktree"));

        monitor.All().Select(r => r.RunId).ShouldBe([here, worktree], ignoreOrder: true);
    }

    [Fact]
    public void a_live_run_is_neither_evicted_nor_counted_against_the_cap()
    {
        // A gate run here is 20-50 minutes; it must not vanish because the suite ran again.
        using var monitor = registry(retainedRuns: 1);
        var live = Guid.NewGuid();
        var newest = Guid.NewGuid();
        monitor.Record(liveRun(live, "Demo", at(0)));
        monitor.Record(run(Guid.NewGuid(), "Demo", at(1)));
        monitor.Record(run(newest, "Demo", at(2)));

        monitor.All().Select(r => r.RunId).ShouldBe([live, newest], ignoreOrder: true);
    }

    [Fact]
    public void zero_disables_the_cap()
    {
        using var monitor = registry(retainedRuns: 0);
        foreach (var index in Enumerable.Range(0, 4)) monitor.Record(run(Guid.NewGuid(), "Demo", at(index)));

        monitor.All().Count.ShouldBe(4);
    }

    [Fact]
    public void a_restart_does_not_restore_the_cards_the_policy_already_evicted()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        using (var first = new MonitorRunRegistry(_dataPath, TimeSpan.Zero))
        {
            foreach (var (id, index) in ids.Select((id, i) => (id, i))) first.Record(run(id, "Demo", at(index)));
            first.All().Count.ShouldBe(4);
        }

        // The cap arrives (configured, or simply applied at boot) and the board comes back bounded.
        using var second = new MonitorRunRegistry(_dataPath, TimeSpan.Zero, retainedRuns: 2);
        second.All().Select(r => r.RunId).ShouldBe([ids[2], ids[3]], ignoreOrder: true);
    }
}
