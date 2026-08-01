namespace Bobcat.Monitor.Coordination;

/// <summary>
/// The one vocabulary for plan enum values on every wire — YAML documents, the HTTP API, and
/// MCP tool results all speak these strings. Two mappings for the same value is how a
/// vocabulary drifts, and the drift stays invisible until a document stops matching.
/// </summary>
public static class PlanWire
{
    public const string KindNames = "issue, pr, publish, consume, test-run-gate";
    public const string BumpNames = "fix, minor, major";
    public const string MergeNames = "manual-review, merge-on-green";

    public static string ToWire(PlanNodeKind kind) => kind switch
    {
        PlanNodeKind.Issue => "issue",
        PlanNodeKind.PullRequest => "pr",
        PlanNodeKind.Publish => "publish",
        PlanNodeKind.Consume => "consume",
        PlanNodeKind.TestRunGate => "test-run-gate",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string ToWire(BumpKind bump) => bump switch
    {
        BumpKind.Fix => "fix",
        BumpKind.Minor => "minor",
        BumpKind.Major => "major",
        _ => throw new ArgumentOutOfRangeException(nameof(bump))
    };

    public static string ToWire(MergePolicy merge) => merge switch
    {
        MergePolicy.ManualReview => "manual-review",
        MergePolicy.MergeOnGreen => "merge-on-green",
        _ => throw new ArgumentOutOfRangeException(nameof(merge))
    };

    public static bool TryKind(string value, out PlanNodeKind kind)
    {
        switch (value)
        {
            case "issue": kind = PlanNodeKind.Issue; return true;
            case "pr": kind = PlanNodeKind.PullRequest; return true;
            case "publish": kind = PlanNodeKind.Publish; return true;
            case "consume": kind = PlanNodeKind.Consume; return true;
            case "test-run-gate": kind = PlanNodeKind.TestRunGate; return true;
            default: kind = default; return false;
        }
    }

    public static bool TryBump(string value, out BumpKind bump)
    {
        switch (value)
        {
            case "fix": bump = BumpKind.Fix; return true;
            case "minor": bump = BumpKind.Minor; return true;
            case "major": bump = BumpKind.Major; return true;
            default: bump = default; return false;
        }
    }

    public static bool TryMerge(string value, out MergePolicy merge)
    {
        switch (value)
        {
            case "manual-review": merge = MergePolicy.ManualReview; return true;
            case "merge-on-green": merge = MergePolicy.MergeOnGreen; return true;
            default: merge = default; return false;
        }
    }
}
