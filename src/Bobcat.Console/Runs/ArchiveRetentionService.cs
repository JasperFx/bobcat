namespace Bobcat.Console.Runs;

/// <summary>
/// Periodically ages the archive directory on a long-lived monitor — the boot-time sweep in
/// the registry's constructor only helps a monitor that restarts. Hourly is plenty: retention
/// is measured in days, and the sweep is a directory scan plus an mtime check per file.
/// </summary>
/// <remarks>
/// It bounds the board's run count on the same tick (issue #198). That sweep's real trigger is
/// a run finishing, which the registry does inline; this pass exists for the case that has no
/// such moment — an orphan that only became evictable because a restart declared it one.
/// </remarks>
public class ArchiveRetentionService : BackgroundService
{
    private static readonly TimeSpan interval = TimeSpan.FromHours(1);

    private readonly MonitorRunRegistry _registry;
    private readonly ILogger<ArchiveRetentionService> _logger;

    public ArchiveRetentionService(MonitorRunRegistry registry, ILogger<ArchiveRetentionService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_registry.Retention <= TimeSpan.Zero && _registry.RetainedRuns <= 0) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                var (ejected, deleted) = _registry.SweepAging();
                if (ejected + deleted > 0)
                {
                    _logger.LogInformation(
                        "Archive aging: ejected {Ejected} stale run(s), deleted {Deleted} ejected archive(s) older than {Retention}",
                        ejected, deleted, _registry.Retention);
                }

                var evicted = _registry.SweepRetainedRuns();
                if (evicted.Count > 0)
                {
                    _logger.LogInformation(
                        "Run retention: ejected {Count} run(s) beyond the most recent {Retained} of their suite; archives kept on disk",
                        evicted.Count, _registry.RetainedRuns);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Archive aging sweep failed");
            }
        }
    }
}
