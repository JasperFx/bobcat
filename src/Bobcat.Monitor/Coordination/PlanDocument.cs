namespace Bobcat.Monitor.Coordination;

/// <summary>
/// A parsed plan document — the source of truth for one coordination DAG
/// (docs/agent-coordination-design.md). The YAML file is the wire contract; these records are
/// the monitor's read of it. GitHub remains the system of record for the artifacts the nodes
/// point at — a plan declares intent and dependency structure, never live status.
/// </summary>
public record PlanDocument
{
    /// <summary>Schema version. Only 1 exists.</summary>
    public required int Schema { get; init; }

    /// <summary>The plan's slug — its identity everywhere (streams, MCP tools, UI routes).</summary>
    public required string Plan { get; init; }

    public required string Title { get; init; }

    /// <summary>
    /// The anchoring GitHub issue ("org/repo#n") — the plan's human face for discussion and
    /// closure. Optional until the planning repo exists.
    /// </summary>
    public string? Anchor { get; init; }

    /// <summary>Repo aliases (alias → "org/name") so node bodies stay terse.</summary>
    public required IReadOnlyDictionary<string, string> Repos { get; init; }

    /// <summary>Nodes in document order. Every Repo value is already alias-resolved.</summary>
    public required IReadOnlyList<PlanNode> Nodes { get; init; }

    /// <summary>
    /// Nodes in dependency order (a node always follows everything it depends on). Computed
    /// during parsing — a document with a dependency cycle never parses.
    /// </summary>
    public required IReadOnlyList<PlanNode> InDependencyOrder { get; init; }

    public PlanNode? FindNode(string id) => Nodes.FirstOrDefault(x => x.Id == id);
}

/// <summary>
/// Wire kinds: issue, pr, publish, consume, test-run-gate. Publish/consume/upgrade steps are
/// first-class nodes precisely because they have no GitHub identity — the cross-repo release
/// train is the gap none of the surveyed prior art models.
/// </summary>
public enum PlanNodeKind
{
    Issue,
    PullRequest,
    Publish,
    Consume,
    TestRunGate
}

/// <summary>Wire values: fix, minor, major. Declared on publish nodes, consumed by whatever
/// executes the publish; the monitor reports declared bump vs. observed version as a fault
/// when they disagree, never silently reconciled.</summary>
public enum BumpKind
{
    Fix,
    Minor,
    Major
}

/// <summary>
/// Wire values: manual-review, merge-on-green. Merge-on-green rides GitHub's native
/// auto-merge — the monitor visualizes policy and status, it is not a merge bot.
/// </summary>
public enum MergePolicy
{
    ManualReview,
    MergeOnGreen
}

public record PlanNode
{
    public required string Id { get; init; }
    public required PlanNodeKind Kind { get; init; }

    /// <summary>Defaults to the Id when the document gives none.</summary>
    public required string Title { get; init; }

    /// <summary>Resolved "org/name". Required for issue/pr/consume nodes, absent otherwise —
    /// a publish node's identity is package + feed, and a test-run-gate correlates by
    /// BOBCAT_PLAN_NODE rather than by repository.</summary>
    public string? Repo { get; init; }

    /// <summary>
    /// Issue number, when the node is already bound to a real GitHub issue. A planned issue
    /// may be unbound — an agent materializes it later and reports the linkage. Status stays
    /// "unrealized" until then.
    /// </summary>
    public int? Issue { get; init; }

    /// <summary>PR number for pr nodes; like Issue, optional until the PR exists.</summary>
    public int? PullRequest { get; init; }

    /// <summary>Merge policy for issue/pr nodes. Defaults to manual review — an agent's work
    /// merging without a human in the loop is opt-in per node, never ambient.</summary>
    public MergePolicy? Merge { get; init; }

    /// <summary>Package id for publish/consume nodes.</summary>
    public string? Package { get; init; }

    /// <summary>
    /// Feed NAME for publish nodes, defaulting to "nuget.org". Names resolve to URLs and
    /// credentials in monitor configuration — a plan document never carries a secret.
    /// </summary>
    public string? Feed { get; init; }

    public BumpKind? Bump { get; init; }

    /// <summary>
    /// Optional exact version for publish nodes — when the author knows the target, say it,
    /// and the watcher looks for exactly that instead of deriving expectation from Bump plus
    /// the observed baseline.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>Ids of the nodes this one depends on.</summary>
    public required IReadOnlyList<string> DependsOn { get; init; }
}
