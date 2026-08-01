namespace Bobcat.Monitor.Coordination.GitHub;

/// <summary>The one HTTP seam: POST a GraphQL query, return the raw response JSON.</summary>
public interface IGitHubQueryClient
{
    Task<string> PostQueryAsync(string query, CancellationToken ct);
}

/// <summary>
/// One sweep: collect every bound issue/pr reference from the valid registered plans, query
/// each repository once, fold the observations into the cache. Orchestration only — the wire
/// shapes live in <see cref="GitHubGraph"/>, the HTTP in <see cref="IGitHubQueryClient"/>.
/// </summary>
public class GitHubPoller
{
    private readonly PlanRegistry _plans;
    private readonly GitHubStatusCache _cache;
    private readonly PackagePinCache _pins;
    private readonly IGitHubQueryClient _client;
    private readonly ILogger<GitHubPoller> _logger;

    public GitHubPoller(
        PlanRegistry plans,
        GitHubStatusCache cache,
        PackagePinCache pins,
        IGitHubQueryClient client,
        ILogger<GitHubPoller> logger)
    {
        _plans = plans;
        _cache = cache;
        _pins = pins;
        _client = client;
        _logger = logger;
    }

    /// <summary>Every "org/repo" → bound numbers pair the current plans reference.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<int>> CollectReferences(IEnumerable<RegisteredPlan> plans)
    {
        var byRepo = new Dictionary<string, SortedSet<int>>();

        foreach (var plan in plans)
        {
            if (plan.Document is not { } document) continue;

            foreach (var node in document.Nodes)
            {
                var number = node.Kind switch
                {
                    PlanNodeKind.Issue => node.Issue,
                    PlanNodeKind.PullRequest => node.PullRequest,
                    _ => null
                };

                if (number is null || node.Repo is null) continue;

                if (!byRepo.TryGetValue(node.Repo, out var numbers))
                {
                    byRepo[node.Repo] = numbers = [];
                }

                numbers.Add(number.Value);
            }
        }

        return byRepo.ToDictionary(x => x.Key, x => (IReadOnlyCollection<int>)x.Value);
    }

    /// <summary>Every "org/repo" → package set the consume nodes want pin observations for.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> CollectConsumeReferences(
        IEnumerable<RegisteredPlan> plans)
    {
        var byRepo = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var plan in plans)
        {
            if (plan.Document is not { } document) continue;

            foreach (var node in document.Nodes)
            {
                if (node.Kind != PlanNodeKind.Consume || node.Repo is null || node.Package is null) continue;

                if (!byRepo.TryGetValue(node.Repo, out var packages))
                {
                    byRepo[node.Repo] = packages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                packages.Add(node.Package);
            }
        }

        return byRepo.ToDictionary(x => x.Key, x => (IReadOnlyCollection<string>)x.Value);
    }

    /// <summary>
    /// Returns how many observations changed. A repository that fails to answer keeps its
    /// last observations — a poll failure is absence of NEW evidence, not evidence that the
    /// old state went away (the same stance as a crashed worker's Indeterminate).
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
        => await sweepIssues(ct) + await sweepPins(ct);

    private async Task<int> sweepIssues(CancellationToken ct)
    {
        var changed = 0;

        foreach (var (repo, numbers) in CollectReferences(_plans.All()))
        {
            var parts = repo.Split('/');
            var (owner, name) = (parts[0], parts[1]);

            try
            {
                var query = GitHubGraph.BuildQuery(owner, name, numbers);
                var response = await _client.PostQueryAsync(query, ct);

                foreach (var observation in GitHubGraph.ParseResponse(owner, name, response, DateTimeOffset.UtcNow))
                {
                    if (_cache.Upsert(observation)) changed++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "GitHub sweep failed for {Repo} ({Count} references)", repo, numbers.Count);
            }
        }

        return changed;
    }

    private async Task<int> sweepPins(CancellationToken ct)
    {
        var changed = 0;

        foreach (var (repo, packages) in CollectConsumeReferences(_plans.All()))
        {
            var parts = repo.Split('/');
            var (owner, name) = (parts[0], parts[1]);

            try
            {
                var response = await _client.PostQueryAsync(PackagePins.BuildQuery(owner, name), ct);
                var files = PackagePins.ParseResponse(response);
                var observedAt = DateTimeOffset.UtcNow;

                foreach (var package in packages)
                {
                    var (version, source) = PackagePins.FindPin(files, package);
                    if (_pins.Upsert(new PackagePin(repo, package, version, source, observedAt))) changed++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Package pin sweep failed for {Repo}", repo);
            }
        }

        return changed;
    }
}
