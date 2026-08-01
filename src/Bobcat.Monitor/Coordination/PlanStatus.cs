using Bobcat.Monitor.Coordination.GitHub;

namespace Bobcat.Monitor.Coordination;

public record NodeStatusView(
    string Id,
    string Kind,
    string Title,
    string Status,
    bool Ready,
    string? Ref,
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
/// Derives live node status from a plan plus GitHub observations — a pure function, and
/// deliberately derivation-first (docs/agent-coordination-design.md): anything a poll can
/// observe comes from the poll, so a crashed agent's issue can never render "in progress"
/// forever. Status vocabulary, in decision order per node:
///
///  - <c>unrealized</c> — an issue/pr node not yet bound to a real GitHub number. Ready
///    unrealized work means "materialize the issue and start".
///  - <c>unknown</c> — bound, but GitHub hasn't been observed yet (no token, first sweep
///    pending), or a kind whose observer doesn't exist yet (publish/consume/test-run-gate).
///  - <c>missing</c> — GitHub says the reference points at nothing: a wiring mistake to
///    surface, never a row to skip.
///  - <c>done</c> — issue closed / PR merged.
///  - <c>abandoned</c> — PR closed without merging. NOT done: it still blocks downstream,
///    which is exactly right.
///  - <c>pr-open</c> — an open PR declares it closes this issue.
///  - <c>claimed</c> — the agent:working label (set via the future claim_issue tool) or a
///    human assignee.
///  - <c>open</c> — observed, nobody on it.
///
/// Ready = not done, and every dependency done. Blocked-but-claimed and similar composites
/// are the UI's business to render, not extra states here.
/// </summary>
public static class PlanStatus
{
    /// <summary>The label an agent (or human) uses to claim an issue for in-flight work.</summary>
    public const string ClaimedLabel = "agent:working";

    public static PlanStatusView For(RegisteredPlan plan, GitHubStatusCache observations)
    {
        var document = plan.Document
                       ?? throw new ArgumentException($"plan '{plan.Slug}' has no valid document", nameof(plan));

        var nodes = new List<NodeStatusView>();
        var done = new HashSet<string>();

        // InDependencyOrder so each node's dependencies are already classified when it is.
        foreach (var node in document.InDependencyOrder)
        {
            var view = statusOf(node, observations);
            if (view.Status == "done") done.Add(node.Id);

            // "missing" is excluded: an agent can't work a reference that points at nothing —
            // the actionable thing is fixing the plan, and the status itself says so.
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

    private static NodeStatusView statusOf(PlanNode node, GitHubStatusCache observations)
    {
        var (status, @ref, observation) = classify(node, observations);

        return new NodeStatusView(
            node.Id,
            PlanWire.ToWire(node.Kind),
            node.Title,
            status,
            Ready: false,
            @ref,
            observation?.Title,
            observation?.Assignees is { Count: > 0 } assignees ? assignees : null,
            observation?.ClosingPrs is { Count: > 0 } prs
                ? prs.Where(x => x.State == "open").Select(x => x.Number).ToArray()
                : null,
            observation?.ObservedAt,
            node.DependsOn);
    }

    private static (string Status, string? Ref, GitHubObservation? Observation) classify(
        PlanNode node, GitHubStatusCache observations)
    {
        switch (node.Kind)
        {
            case PlanNodeKind.Issue:
            case PlanNodeKind.PullRequest:
            {
                var number = node.Kind == PlanNodeKind.Issue ? node.Issue : node.PullRequest;
                if (number is null) return ("unrealized", null, null);

                var @ref = $"{node.Repo}#{number}";
                var observation = observations.Find(@ref);
                if (observation is null) return ("unknown", @ref, null);

                return (classifyObserved(node.Kind, observation), @ref, observation);
            }

            default:
                // publish/consume/test-run-gate observers arrive with the NuGet watcher and
                // the run-gate linkage; until then honesty is "unknown", not a guess.
                return ("unknown", null, null);
        }
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
}
