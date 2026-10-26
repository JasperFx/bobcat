using Bobcat.Supervisor.Tests;
using Bobcat.Ui.Contracts;
using Bobcat.Ui.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// bobcat#784: a run whose publisher dies while the console is up. The registry rehydrated such a
/// run as orphaned only after a restart; now a live run silent for <see cref="MonitorRunRegistry.OrphanAfter"/>
/// is orphaned by the sweep, pushed to the browser, and ejectable. Driven on a fake clock, because
/// the interval is seconds and a test that sleeps is a test that flakes.
/// </summary>
public class OrphanDetectionTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-orphans-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new();

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private MonitorRunRegistry newRegistry(TimeSpan? orphanAfter = null)
        => new(_dataPath, retention: TimeSpan.Zero, retainedRuns: 0, orphanAfter: orphanAfter ?? TimeSpan.FromSeconds(30), time: _clock);

    private static RunStarted started(Guid runId, DateTimeOffset at) => new(runId, "Demo", "/repo", "main", "in-process", at, 2);

    [Fact]
    public void a_live_run_silent_past_the_interval_is_orphaned_and_a_finished_one_never_is()
    {
        using var registry = newRegistry();
        var silent = Guid.NewGuid();
        var finished = Guid.NewGuid();
        registry.Record([started(silent, _clock.GetUtcNow()), started(finished, _clock.GetUtcNow())]);
        registry.Record([new RunFinished(finished, 0, 2, 0, 0, 0, _clock.GetUtcNow())]);

        _clock.Advance(TimeSpan.FromSeconds(29));
        registry.SweepOrphans().ShouldBeEmpty();
        registry.Find(silent)!.Orphaned.ShouldBeFalse();

        _clock.Advance(TimeSpan.FromSeconds(2));
        var orphaned = registry.SweepOrphans().Single();
        orphaned.RunId.ShouldBe(silent);
        orphaned.LastEventAt.ShouldBe(_clock.GetUtcNow() - TimeSpan.FromSeconds(31));
        registry.Find(silent)!.Orphaned.ShouldBeTrue();
        registry.Find(finished)!.Orphaned.ShouldBeFalse();

        // Once: the next sweep has nothing new to say about it.
        registry.SweepOrphans().ShouldBeEmpty();
    }

    [Fact]
    public void any_event_keeps_a_run_alive_and_un_orphans_one_the_sweep_gave_up_on()
    {
        using var registry = newRegistry();
        var runId = Guid.NewGuid();
        registry.Record([started(runId, _clock.GetUtcNow())]);

        // A step event carries no stamp of its own, and still counts: the console heard it now.
        _clock.Advance(TimeSpan.FromSeconds(20));
        registry.Record([new StepStarted(runId, "F/a", "s1", "Given", "a thing")]);
        _clock.Advance(TimeSpan.FromSeconds(20));
        registry.SweepOrphans().ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(31));
        registry.SweepOrphans().Count.ShouldBe(1);

        // The publisher was only slow.
        registry.Record([new RunHeartbeat(runId, _clock.GetUtcNow())]);
        registry.Find(runId)!.Orphaned.ShouldBeFalse();
        registry.Find(runId)!.LastEventAt.ShouldBe(_clock.GetUtcNow());
    }

    [Fact]
    public void the_console_stamps_its_own_clock_on_a_live_event_not_the_publishers()
    {
        // A publisher on another box may keep another clock; the sweep compares against this one.
        using var registry = newRegistry();
        var runId = Guid.NewGuid();
        registry.Record([started(runId, _clock.GetUtcNow().AddHours(-3))]);

        registry.Find(runId)!.LastEventAt.ShouldBe(_clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(10));
        registry.SweepOrphans().ShouldBeEmpty();
    }

    [Fact]
    public void a_rehydrated_run_is_last_heard_from_when_its_publisher_last_spoke()
    {
        var runId = Guid.NewGuid();
        var spoke = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        using (var first = newRegistry())
        {
            first.Record([
                started(runId, spoke.AddMinutes(-1)),
                new ScenarioStarted(runId, "F/a", "F", "a", 1, spoke),
                new StepStarted(runId, "F/a", "s1", "Given", "a thing")
            ]);
        }

        using var restarted = newRegistry();
        var run = restarted.Find(runId).ShouldNotBeNull();
        run.Orphaned.ShouldBeTrue();
        // Not the restart's clock, and not the unstamped step either: the last event that said when.
        run.LastEventAt.ShouldBe(spoke);
        RunEndpoints.All(restarted).Single().LastEventAt.ShouldBe(spoke);
    }

    [Fact]
    public void zero_turns_live_detection_off()
    {
        using var registry = newRegistry(TimeSpan.Zero);
        registry.Record([started(Guid.NewGuid(), _clock.GetUtcNow())]);
        _clock.Advance(TimeSpan.FromDays(1));

        registry.OrphanAfter.ShouldBe(TimeSpan.Zero);
        registry.SweepOrphans().ShouldBeEmpty();
        registry.All().Single().Orphaned.ShouldBeFalse();
    }

    [Fact]
    public void an_orphaned_run_can_be_ejected_in_bulk_like_a_finished_one()
    {
        using var registry = newRegistry();
        var runId = Guid.NewGuid();
        registry.Record([started(runId, _clock.GetUtcNow())]);
        registry.RemoveWhere(_ => true).ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(31));
        registry.SweepOrphans();

        registry.RemoveWhere(_ => true).ShouldBe([runId]);
    }

    [Fact]
    public void the_sweep_pushes_each_newly_orphaned_run_to_the_browser()
    {
        using var registry = newRegistry();
        var accumulator = new SignalRBatchAccumulator(new ServiceCollection().BuildServiceProvider(), NullLogger<SignalRBatchAccumulator>.Instance);
        var service = new OrphanSweepService(registry, accumulator, NullLogger<OrphanSweepService>.Instance, _clock);
        var runId = Guid.NewGuid();
        registry.Record([started(runId, _clock.GetUtcNow())]);

        service.SweepOnce();
        accumulator.DrainOnce().ShouldBeNull();

        _clock.Advance(TimeSpan.FromSeconds(31));
        service.SweepOnce();

        var item = accumulator.DrainOnce().ShouldNotBeNull().Items.ShouldHaveSingleItem();
        item.Type.ShouldBe("run_orphaned");
        var notice = item.Data.ShouldBeOfType<RunOrphaned>();
        notice.RunId.ShouldBe(runId);
        notice.LastEventAt.ShouldBe(_clock.GetUtcNow() - TimeSpan.FromSeconds(31));

        // And nothing twice.
        service.SweepOnce();
        accumulator.DrainOnce().ShouldBeNull();
    }

    [Fact]
    public void the_sweep_runs_a_third_of_the_interval_apart_and_never_more_than_once_a_second()
    {
        OrphanSweepService.PeriodFor(TimeSpan.FromSeconds(30)).ShouldBe(TimeSpan.FromSeconds(10));
        OrphanSweepService.PeriodFor(TimeSpan.FromSeconds(2)).ShouldBe(TimeSpan.FromSeconds(1));
    }
}
