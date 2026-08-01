using Bobcat.Monitor.Coordination.GitHub;
using Bobcat.Monitor.Coordination.NuGet;

namespace Bobcat.Monitor.Coordination;

/// <summary>Everything the status derivation reads — one aggregate so the read side has one
/// dependency instead of four, registered as a singleton over the individual caches.</summary>
public record ObservationStores(
    GitHubStatusCache GitHub,
    PackagePinCache Pins,
    NuGetStatusCache NuGet,
    NuGetBaselineStore Baselines);

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
///    kind whose observer doesn't exist yet (test-run-gate).
///  - <c>missing</c> — the reference points at nothing: a nonexistent issue, or a feed name
///    nobody configured. A wiring mistake to surface, never a row to skip — and never ready,
///    because the actionable thing is fixing the plan, not dispatching an agent.
///  - <c>done</c> — issue closed / PR merged / declared version or bump observed on the
///    feed / the downstream repo's committed pin caught up to what was published.
///  - <c>abandoned</c> — PR closed without merging. NOT done: it still blocks downstream,
///    which is exactly right.
///  - <c>mismatch</c> — the feed moved but not the way the plan declared (a fix release
///    against a declared minor bump). Reported, never silently reconciled.
///  - <c>pr-open</c> — an open PR declares it closes this issue.
///  - <c>claimed</c> — the agent:working label (set via the future claim_issue tool) or a
///    human assignee.
///  - <c>open</c> / <c>waiting</c> — observed, nothing to act on yet (issues wait for
///    someone; publish and consume nodes wait for evidence).
///
/// Ready = not done/missing, and every dependency done.
/// </summary>
public static class PlanStatus
{
    /// <summary>The label an agent (or human) uses to claim an issue for in-flight work.</summary>
    public const string ClaimedLabel = "agent:working";

    public static PlanStatusView For(RegisteredPlan plan, ObservationStores stores)
    {
        var document = plan.Document
                       ?? throw new ArgumentException($"plan '{plan.Slug}' has no valid document", nameof(plan));

        var nodes = new List<NodeStatusView>();
        var done = new HashSet<string>();
        // Publish nodes that reached done, with the version that satisfied them — the
        // comparison target for downstream consume nodes.
        var published = new Dictionary<string, (string Package, PackageVersion Version)>();

        // InDependencyOrder so each node's dependencies (and any upstream publish's observed
        // version) are already classified when the node is.
        foreach (var node in document.InDependencyOrder)
        {
            var view = statusOf(plan.Slug, document, node, stores, published);

            if (view.Status == "done")
            {
                done.Add(node.Id);
            }

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
        string plan,
        PlanDocument document,
        PlanNode node,
        ObservationStores stores,
        Dictionary<string, (string Package, PackageVersion Version)> published)
    {
        return node.Kind switch
        {
            PlanNodeKind.Issue or PlanNodeKind.PullRequest => gitHubNode(node, stores.GitHub),
            PlanNodeKind.Publish => publishNode(plan, node, stores, published),
            PlanNodeKind.Consume => consumeNode(document, node, stores.Pins, published),
            // The test-run-gate observer arrives with the BOBCAT_PLAN_NODE linkage; until
            // then honesty is "unknown", not a guess.
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
        string plan,
        PlanNode node,
        ObservationStores stores,
        Dictionary<string, (string Package, PackageVersion Version)> published)
    {
        var @ref = $"{node.Feed}/{node.Package}";
        var observation = stores.NuGet.Find(node.Feed!, node.Package!);

        if (observation is null) return bare(node, "unknown", @ref, null);
        if (observation.Error is not null) return bare(node, "missing", @ref, observation.Error, observation.ObservedAt);

        var versions = observation.Versions
            .Select(PackageVersion.TryParse)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
        var latest = versions.Length > 0 ? versions.Max() : null;

        var (status, detail, doneVersion) = classifyPublish(plan, node, versions, latest, stores.Baselines);

        if (doneVersion is not null) published[node.Id] = (node.Package!, doneVersion);

        return bare(node, status, @ref, detail, observation.ObservedAt);
    }

    private static (string Status, string Detail, PackageVersion? DoneVersion) classifyPublish(
        string plan, PlanNode node, PackageVersion[] versions, PackageVersion? latest, NuGetBaselineStore baselines)
    {
        // An exact declared version outranks bump derivation: the author said the target.
        if (node.Version is not null)
        {
            var declared = versions.FirstOrDefault(x => x.Text == node.Version);
            return declared is not null
                ? ("done", $"observed {node.Version}", declared)
                : ("waiting", $"waiting for {node.Version}" + (latest is null ? "" : $" — latest is {latest}"), null);
        }

        var baseline = baselines.TryGet(plan, node.Id);
        if (baseline is null) return ("unknown", "baseline pending first feed observation", null);

        if (baseline.Length == 0)
        {
            // The package did not exist when watching began: any version is the first publish.
            return latest is null
                ? ("waiting", "waiting for the first publish", null)
                : ("done", $"first publish observed: {latest}", latest);
        }

        var baselineVersion = PackageVersion.TryParse(baseline)!;
        var newer = versions.Where(x => x.CompareTo(baselineVersion) > 0).ToArray();
        var satisfying = newer.Where(x => x.SatisfiesBumpFrom(baselineVersion, node.Bump!.Value)).ToArray();

        if (satisfying.Length > 0)
        {
            var best = satisfying.Max()!;
            return ("done", $"observed {best} ({PlanWire.ToWire(node.Bump!.Value)} above {baseline})", best);
        }

        if (newer.Length > 0)
        {
            return ("mismatch",
                $"declared a {PlanWire.ToWire(node.Bump!.Value)} bump from {baseline} but observed only "
                + string.Join(", ", newer.Select(x => x.Text)),
                null);
        }

        return ("waiting", $"waiting for a {PlanWire.ToWire(node.Bump!.Value)} bump above {baseline}", null);
    }

    /// <summary>
    /// Done = the downstream repo's committed pin caught up to what an upstream publish node
    /// actually shipped — two observations agreeing, no assertion anywhere. Without a done
    /// upstream publish for the same package there is no target yet, so the node waits
    /// (blocked by its dependencies as usual); with no publish dependency at all the plan
    /// gives the watcher nothing to compare against, and that surfaces as its own detail.
    /// </summary>
    private static NodeStatusView consumeNode(
        PlanDocument document,
        PlanNode node,
        PackagePinCache pins,
        Dictionary<string, (string Package, PackageVersion Version)> published)
    {
        var @ref = $"{node.Repo}:{node.Package}";
        var pin = pins.Find(node.Repo!, node.Package!);
        if (pin is null) return bare(node, "unknown", @ref, null);

        var pinned = PackageVersion.TryParse(pin.Version);
        var pinnedText = pin.Version ?? "nothing";

        var targets = node.DependsOn
            .Select(published.GetValueOrDefault)
            .Where(x => string.Equals(x.Package, node.Package, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Version)
            .ToArray();

        if (targets.Length == 0)
        {
            var hasPublishDependency = node.DependsOn
                .Select(document.FindNode)
                .Any(x => x is { Kind: PlanNodeKind.Publish }
                          && string.Equals(x.Package, node.Package, StringComparison.OrdinalIgnoreCase));

            return bare(node,
                hasPublishDependency ? "waiting" : "unknown",
                @ref,
                hasPublishDependency
                    ? $"pinned {pinnedText}; upstream publish not observed done yet"
                    : $"no upstream publish node for {node.Package} — nothing to compare the pin against",
                pin.ObservedAt);
        }

        var target = targets.Max()!;

        return pinned is not null && pinned.CompareTo(target) >= 0
            ? bare(node, "done", @ref, $"pinned {pinned} (published {target})", pin.ObservedAt)
            : bare(node, "waiting", @ref, $"pinned {pinnedText}, waiting for {target}", pin.ObservedAt);
    }

    private static NodeStatusView bare(
        PlanNode node, string status, string? @ref, string? detail, DateTimeOffset? observedAt = null)
        => new(
            node.Id, PlanWire.ToWire(node.Kind), node.Title, status, Ready: false,
            @ref, detail, null, null, null, observedAt, node.DependsOn);
}
