namespace Bobcat.Monitor.Coordination.NuGet;

/// <summary>
/// One sweep: every (feed, package) pair the plans' publish nodes reference gets queried
/// once, observations fold into the cache, and any publish node without a baseline gets one
/// captured from what its feed just answered. Baseline capture lives HERE, not in the status
/// derivation — <see cref="PlanStatus"/> stays a pure function of plan + observations.
/// </summary>
public class NuGetPoller
{
    private readonly PlanRegistry _plans;
    private readonly NuGetFeeds _feeds;
    private readonly NuGetStatusCache _cache;
    private readonly NuGetBaselineStore _baselines;
    private readonly ILogger<NuGetPoller> _logger;

    public NuGetPoller(
        PlanRegistry plans,
        NuGetFeeds feeds,
        NuGetStatusCache cache,
        NuGetBaselineStore baselines,
        ILogger<NuGetPoller> logger)
    {
        _plans = plans;
        _feeds = feeds;
        _cache = cache;
        _baselines = baselines;
        _logger = logger;
    }

    private record PublishRef(string Plan, string NodeId, string Feed, string Package);

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var references = collectPublishNodes();
        var changed = 0;

        foreach (var group in references.GroupBy(x => NuGetObservation.KeyFor(x.Feed, x.Package)))
        {
            var first = group.First();
            var observation = await observe(first.Feed, first.Package, ct);
            if (observation is null) continue; // transient failure — last observation stands

            if (_cache.Upsert(observation)) changed++;

            if (observation.Error is not null) continue;

            foreach (var reference in group)
            {
                if (_baselines.TryGet(reference.Plan, reference.NodeId) is not null) continue;

                // Highest version on the feed right now — or empty for a package that does
                // not exist yet, the first-ever-publish case where any version will satisfy.
                var baseline = observation.Versions
                    .Select(PackageVersion.TryParse)
                    .Where(x => x is not null)
                    .Max();

                _baselines.Capture(reference.Plan, reference.NodeId, baseline?.Text ?? "");
            }
        }

        return changed;
    }

    private IReadOnlyList<PublishRef> collectPublishNodes()
    {
        var references = new List<PublishRef>();

        foreach (var plan in _plans.All())
        {
            if (plan.Document is not { } document) continue;

            foreach (var node in document.Nodes)
            {
                if (node.Kind != PlanNodeKind.Publish) continue;
                references.Add(new PublishRef(plan.Slug, node.Id, node.Feed!, node.Package!));
            }
        }

        return references;
    }

    private async Task<NuGetObservation?> observe(string feedName, string package, CancellationToken ct)
    {
        var feed = _feeds.Resolve(feedName);
        if (feed is null)
        {
            // Not transient — a plan naming a feed nobody configured is a wiring fault the
            // status view must surface.
            return new NuGetObservation(
                feedName, package, [],
                $"feed '{feedName}' is not configured (Monitor:Feeds:{feedName})",
                DateTimeOffset.UtcNow);
        }

        try
        {
            var versions = await feed.GetVersionsAsync(package, ct);
            return new NuGetObservation(feedName, package, versions, null, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "NuGet sweep failed for {Package} on feed {Feed}", package, feedName);
            return null;
        }
    }
}

/// <summary>Periodic sweeps, default 60s (<c>Monitor:NuGetPollSeconds</c>). Always on — the
/// default nuget.org feed needs no credentials, and a sweep with no publish nodes is free.</summary>
public class NuGetPollingService : BackgroundService
{
    private readonly NuGetPoller _poller;
    private readonly TimeSpan _interval;
    private readonly ILogger<NuGetPollingService> _logger;

    public NuGetPollingService(NuGetPoller poller, IConfiguration configuration, ILogger<NuGetPollingService> logger)
    {
        _poller = poller;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(configuration.GetValue("Monitor:NuGetPollSeconds", 60d));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var changed = await _poller.SweepAsync(stoppingToken);
                if (changed > 0) _logger.LogInformation("NuGet sweep observed {Count} change(s)", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "NuGet sweep failed");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
