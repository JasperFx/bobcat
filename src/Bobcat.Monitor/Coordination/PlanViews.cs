namespace Bobcat.Monitor.Coordination;

/// <summary>
/// The JSON shapes for plans on the read side, shared by the HTTP API and the MCP tools so an
/// agent and the dashboard see the same plan the same way. Enum values are the
/// <see cref="PlanWire"/> strings — one vocabulary across YAML, HTTP, and MCP.
/// </summary>
public record PlanSummary(
    string Slug,
    string Title,
    string Source,
    string? SourcePath,
    bool Valid,
    int Nodes,
    IReadOnlyList<string> Errors,
    DateTimeOffset LoadedAt);

public record PlanNodeView(
    string Id,
    string Kind,
    string Title,
    string? Repo,
    int? Issue,
    int? Pr,
    string? Merge,
    string? Package,
    string? Feed,
    string? Bump,
    string? Version,
    IReadOnlyList<string> DependsOn);

public record PlanDetail(
    string Slug,
    string Title,
    string? Anchor,
    string Source,
    string? SourcePath,
    bool Valid,
    IReadOnlyList<string> Errors,
    DateTimeOffset LoadedAt,
    IReadOnlyDictionary<string, string>? Repos,
    IReadOnlyList<PlanNodeView>? Nodes,
    IReadOnlyList<string>? DependencyOrder);

public static class PlanViews
{
    public static PlanSummary Summarize(RegisteredPlan plan)
        => new(
            plan.Slug,
            plan.Document?.Title ?? plan.Slug,
            sourceName(plan.Source),
            plan.SourcePath,
            plan.IsValid,
            plan.Document?.Nodes.Count ?? 0,
            plan.Errors,
            plan.LoadedAt);

    public static PlanDetail Detail(RegisteredPlan plan)
        => new(
            plan.Slug,
            plan.Document?.Title ?? plan.Slug,
            plan.Document?.Anchor,
            sourceName(plan.Source),
            plan.SourcePath,
            plan.IsValid,
            plan.Errors,
            plan.LoadedAt,
            plan.Document?.Repos,
            plan.Document?.Nodes.Select(node).ToArray(),
            plan.Document?.InDependencyOrder.Select(x => x.Id).ToArray());

    private static PlanNodeView node(PlanNode x)
        => new(
            x.Id,
            PlanWire.ToWire(x.Kind),
            x.Title,
            x.Repo,
            x.Issue,
            x.PullRequest,
            x.Merge is { } merge ? PlanWire.ToWire(merge) : null,
            x.Package,
            x.Feed,
            x.Bump is { } bump ? PlanWire.ToWire(bump) : null,
            x.Version,
            x.DependsOn);

    private static string sourceName(PlanSource source)
        => source == PlanSource.File ? "file" : "pushed";
}
