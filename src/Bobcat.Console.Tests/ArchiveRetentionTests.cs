using Bobcat.Console.Contracts;
using Bobcat.Console.Runs;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Aging of the archive directory. The mtime of the NDJSON file is the aging clock (every
/// ingested event appends, so a stale file means a publisher that has been gone exactly that
/// long): a stale live archive is ejected like a manual eject, a stale ejected archive is
/// deleted. Nothing is ever deleted straight out of the live folder.
/// </summary>
public class ArchiveRetentionTests : IDisposable
{
    private static readonly TimeSpan retention = TimeSpan.FromDays(14);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-retention-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static MonitorEvent[] smallRun(Guid runId) =>
    [
        new RunStarted(runId, "Demo", "/repo", "main", "in-process", DateTimeOffset.UtcNow, 1),
        new ScenarioStarted(runId, "Calc/adds", "Calc", "adds", 1, DateTimeOffset.UtcNow),
        new ScenarioFinished(runId, "Calc/adds", "CleanPass", 1, 20, null),
        new RunFinished(runId, 0, 1, 0, 0, 0, DateTimeOffset.UtcNow)
    ];

    private void age(string file) => File.SetLastWriteTimeUtc(file, DateTime.UtcNow - retention - TimeSpan.FromDays(1));

    [Fact]
    public void the_boot_sweep_retires_archives_older_than_the_retention_period()
    {
        var runId = Guid.NewGuid();
        using (var first = new MonitorRunRegistry(_dataPath, retention))
        {
            first.Record(smallRun(runId));
        }

        age(Path.Combine(_dataPath, $"{runId}.ndjson"));

        // The sweep runs before rehydration, so the dead run is never loaded at all. Its
        // archive passes through ejected/ with its old mtime intact and the delete pass in
        // the same sweep finishes the job.
        using var restarted = new MonitorRunRegistry(_dataPath, retention);
        restarted.Find(runId).ShouldBeNull();
        File.Exists(restarted.ArchiveFileFor(runId)).ShouldBeFalse();
        File.Exists(restarted.EjectedFileFor(runId)).ShouldBeFalse();
    }

    [Fact]
    public void fresh_archives_and_fresh_ejects_survive_the_sweep_untouched()
    {
        var kept = Guid.NewGuid();
        var ejectedByHand = Guid.NewGuid();

        using var registry = new MonitorRunRegistry(_dataPath, retention);
        registry.Record(smallRun(kept));
        registry.Record(smallRun(ejectedByHand));
        registry.Remove(ejectedByHand);

        registry.SweepAging().ShouldBe((0, 0));

        registry.Find(kept).ShouldNotBeNull();
        File.Exists(registry.ArchiveFileFor(kept)).ShouldBeTrue();
        // A manual eject keeps its data for the rest of the retention window.
        File.Exists(registry.EjectedFileFor(ejectedByHand)).ShouldBeTrue();
    }

    [Fact]
    public void a_live_registry_ejects_a_run_whose_publisher_has_been_gone_past_retention()
    {
        var runId = Guid.NewGuid();
        using var registry = new MonitorRunRegistry(_dataPath, retention);
        registry.Record(smallRun(runId));

        age(registry.ArchiveFileFor(runId));

        // Ejected from the dashboard and, because the mtime rode along, aged out of
        // ejected/ in the same sweep.
        registry.SweepAging().ShouldBe((1, 1));
        registry.Find(runId).ShouldBeNull();
        File.Exists(registry.ArchiveFileFor(runId)).ShouldBeFalse();
        File.Exists(registry.EjectedFileFor(runId)).ShouldBeFalse();
    }

    [Fact]
    public void stale_ejected_archives_age_out_on_their_own_clock()
    {
        var runId = Guid.NewGuid();
        using var registry = new MonitorRunRegistry(_dataPath, retention);
        registry.Record(smallRun(runId));
        registry.Remove(runId);

        age(registry.EjectedFileFor(runId));

        registry.SweepAging().ShouldBe((0, 1));
        File.Exists(registry.EjectedFileFor(runId)).ShouldBeFalse();
    }

    [Fact]
    public void zero_retention_disables_aging_entirely()
    {
        var runId = Guid.NewGuid();
        using var registry = new MonitorRunRegistry(_dataPath, TimeSpan.Zero);
        registry.Record(smallRun(runId));

        age(registry.ArchiveFileFor(runId));

        registry.SweepAging().ShouldBe((0, 0));
        registry.Find(runId).ShouldNotBeNull();
        File.Exists(registry.ArchiveFileFor(runId)).ShouldBeTrue();
    }
}
