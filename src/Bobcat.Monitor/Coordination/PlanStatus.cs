using Bobcat.Monitor.Coordination.GitHub;
using Bobcat.Monitor.Coordination.NuGet;

namespace Bobcat.Monitor.Coordination;

public record NodeStatusView(
    string Id,
    string Kind,
    string Title,
    string Status,
    bool Ready,
    string? Ref,
    string? Detail,
    string? ObservedTitle,
    IReadOnlyList<string>? Assignees,
    IReadOnlyList<int>? OpenPrs,
    DateTimeOffset? ObservedAt,
    IReadOnlyList<string> DependsOn);

public record PlanStatusView(
    string Slug,
    string Title,
    IReadOnlyList<NodeStatusView> Nodes,
    IReadOnlyList<string> Ready);

/// <summary>
/// Derives live node status from a plan plus observations — a pure function, and
/// deliberately derivation-first (docs/agent-coordination-design.md): anything a poll can
/// observe comes from the poll, so a crashed agent's issue can never render "in progress"
/// forever. Status vocabulary, in decision order per node:
///
///  - <c>unrealized</c> — an issue/pr node not yet bound to a real GitHub number. Ready
///    unrealized work means "materialize the issue and start".
///  - <c>unknown</c> — bound, but not observed yet (no token, first sweep pending), or a
///    kind whose observer doesn't exist yet (consume, test-run-gate).
///  - <c>missing</c> — the reference points at nothing: a nonexistent issue, or a feed name
///    nobody configured. A wiring mistake to surface, never a row to skip — and never ready,
///    because the actionable thing is fixing the plan, not dispatching an agent.
///  - <c>done</c> — issue closed / PR merged / the declared version (or bump) observed on
///    the feed.
///  - <c>abandoned</c> — PR closed without merging. NOT done: it still blocks downstream,
///    which is exactly right.
///  - <c>mismatch</c> — the feed moved but not the way the plan declared (a fix release
///    against a declared minor bump). Reported, never silently reconciled.
///  - <c>pr-open</c> — an open PR declares it closes this issue.
///  - <c>claimed</c> — the agent:working label (set via the future claim_issue tool) or a
///    human assignee.
///  - <c>open</c> / <c>waiting</c> — observed, nothing to act on yet (issues wait for
///    someone; publish nodes wait for the feed).
///
/// Ready = not done/missing, and every dependency done.
/// </summary>
public static class PlanStatus
{
    /// <summary>The label an agent (or human) uses to claim an issue for in-flight work.</summary>
    public const string ClaimedLabel = "agent:working";

    public static PlanStatusView For(
        RegisteredPlan plan,
        GitHubStatusCache gitHub,
        NuGetStatusCache nuGet,
        NuGetBaselineStore baselines)
    {
        var document = plan.Document
                       ?? throw new ArgumentException($"plan '{plan.Slug}' has no valid document", nameof(plan));

        var nodes = new List<NodeStatusView>();
        var done = new HashSet<string>();

        // InDependencyOrder so each node's dependencies are already classified when it is.
        foreach (var node in document.InDependencyOrder)
        {
            var view = statusOf(plan.Slug, node, gitHub, nuGet, baselines);
            if (view.Status == "done") done.Add(node.Id);

            var ready = view.Status is not ("done" or "missing") && node.DependsOn.All(done.Contains);
            nodes.Add(view with { Ready = ready });
        }

        // Present in document order — dependency order is a computation detail, and the
        // document's order is the author's narrative.
        var byId = nodes.ToDictionary(x => x.Id);
        var presented = document.Nodes.Select(x => byId[x.Id]).ToArray();

        return new PlanStatusView(
            plan.Slug,
            document.Title,
            presented,
            presented.Where(x => x.Ready).Select(x => x.Id).ToArray());
    }

    private static NodeStatusView statusOf(
        string plan, PlanNode node, GitHubStatusCache gitHub, NuGetStatusCache nuGet, NuGetBaselineStore baselines)
    {
        return node.Kind switch
        {
            PlanNodeKind.Issue or PlanNodeKind.PullRequest => gitHubNode(node, gitHub),
            PlanNodeKind.Publish => publishNode(plan, node, nuGet, baselines),
            // consume and test-run-gate observers arrive with the repo-config watcher and
            // the run-gate linkage; until then honesty is "unknown", not a guess.
            _ => bare(node, "unknown", null, null)
        };
    }

    private static NodeStatusView gitHubNode(PlanNode node, GitHubStatusCache observations)
    {
        var number = node.Kind == PlanNodeKind.Issue ? node.Issue : node.PullRequest;
        if (number is null) return bare(node, "unrealized", null, null);

        var @ref = $"{node.Repo}#{number}";
        var observation = observations.Find(@ref);
        if (observation is null) return bare(node, "unknown", @ref, null);

        var status = classifyObserved(node.Kind, observation);

        return new NodeStatusView(
            node.Id,
            PlanWire.ToWire(node.Kind),
            node.Title,
            status,
            Ready: false,
            @ref,
            Detail: null,
            observation.Title,
            observation.Assignees is { Count: > 0 } assignees ? assignees : null,
            observation.ClosingPrs is { Count: > 0 } prs
                ? prs.Where(x => x.State == "open").Select(x => x.Number).ToArray()
                : null,
            observation.ObservedAt,
            node.DependsOn);
    }

    private static string classifyObserved(PlanNodeKind kind, GitHubObservation observation)
    {
        if (observation.State == "missing") return "missing";

        if (kind == PlanNodeKind.PullRequest)
        {
            return observation.State switch
            {
                "merged" => "done",
                "closed" => "abandoned",
                _ => "open"
            };
        }

        if (observation.State == "closed") return "done";
        if (observation.ClosingPrs.Any(x => x.State == "open")) return "pr-open";
        if (observation.Labels.Contains(ClaimedLabel) || observation.Assignees.Count > 0) return "claimed";
        return "open";
    }

    private static NodeStatusView publishNode(
        string plan, PlanNode node, NuGetStatusCache nuGet, NuGetBaselineStore baselines)
    {
        var @ref = $"{node.Feed}/{node.Package}";
        var observation = nuGet.Find(node.Feed!, node.Package!);

        if (observation is null) return bare(node, "unknown", @ref, null);
        if (observation.Error is not null) return bare(node, "missing", @ref, observation.Error, observation.ObservedAt);

        var versions = observation.Versions
            .Select(PackageVersion.TryParse)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
        var latest = versions.Length > 0 ? versions.Max() : null;

        var (status, detail) = classifyPublish(plan, node, versions, latest, baselines);
        return bare(node, status, @ref, detail, observation.ObservedAt);
    }

    private static (string Status, string Detail) classifyPublish(
        string plan, PlanNode node, PackageVersion[] versions, PackageVersion? latest, NuGetBaselineStore baselines)
    {
        // An exact declared version outranks bump derivation: the author said the target.
        if (node.Version is not null)
        {
            return versions.Any(x => x.Text == node.Version)
                ? ("done", $"observed {node.Version}")
                : ("waiting", $"waiting for {node.Version}" + (latest is null ? "" : $" — latest is {latest}"));
        }

        var baseline = baselines.TryGet(plan, node.Id);
        if (baseline is null) return ("unknown", "baseline pending first feed observation");

        if (baseline.Length == 0)
        {
            // The package did not exist when watching began: any version is the first publish.
            return latest is null
                ? ("waiting", "waiting for the first publish")
                : ("done", $"first publish observed: {latest}");
        }

        var baselineVersion = PackageVersion.TryParse(baseline)!;
        var newer = versions.Where(x => x.CompareTo(baselineVersion) > 0).ToArray();
        var satisfying = newer.Where(x => x.SatisfiesBumpFrom(baselineVersion, node.Bump!.Value)).ToArray();

        if (satisfying.Length > 0)
        {
            return ("done", $"observed {satisfying.Max()} ({PlanWire.ToWire(node.Bump!.Value)} above {baseline})");
        }

        if (newer.Length > 0)
        {
            return ("mismatch",
                $"declared a {PlanWire.ToWire(node.Bump!.Value)} bump from {baseline} but observed only "
                + string.Join(", ", newer.Select(x => x.Text)));
        }

        return ("waiting", $"waiting for a {PlanWire.ToWire(node.Bump!.Value)} bump above {baseline}");
    }

    private static NodeStatusView bare(
        PlanNode node, string status, string? @ref, string? detail, DateTimeOffset? observedAt = null)
        => new(
            node.Id, PlanWire.ToWire(node.Kind), node.Title, status, Ready: false,
            @ref, detail, null, null, null, observedAt, node.DependsOn);
}
