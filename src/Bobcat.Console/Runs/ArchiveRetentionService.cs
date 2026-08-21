namespace Bobcat.Console.Runs;

/// <summary>
/// Periodically ages the archive directory on a long-lived monitor — the boot-time sweep in
/// the registry's constructor only helps a monitor that restarts. Hourly is plenty: retention
/// is measured in days, and the sweep is a directory scan plus an mtime check per file.
/// </summary>
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
        if (_registry.Retention <= TimeSpan.Zero) return;

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Archive aging sweep failed");
            }
        }
    }
}
