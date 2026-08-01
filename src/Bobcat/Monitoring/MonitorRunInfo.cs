using System.Reflection;

namespace Bobcat.Monitoring;

/// <summary>
/// Identity and metadata for one run as the monitor sees it. RunId honours
/// <c>BOBCAT_RUN_ID</c> when set — that is how the supervisor groups its worker processes'
/// step streams under one run without any changes here.
/// </summary>
public record MonitorRunInfo(Guid RunId, string Suite, string Repository, string? Branch, string Mode)
{
    public const string RunIdVariable = "BOBCAT_RUN_ID";
    public const string RunOwnerVariable = "BOBCAT_RUN_OWNER";
    public const string PlanNodeVariable = "BOBCAT_PLAN_NODE";

    /// <summary>
    /// True when whatever set <c>BOBCAT_RUN_OWNER</c> owns the run bracket — RunStarted,
    /// heartbeats, RunFinished. A participant process (a supervisor's worker) publishes only
    /// its scenario and step events; without this split, the first worker to finish would
    /// mark the whole shared run finished with its own partial counts. Deliberately a
    /// separate variable from <see cref="RunIdVariable"/>: setting BOBCAT_RUN_ID alone just
    /// pins a run's identity, and that run still owns its bracket.
    /// </summary>
    public bool HasExternalOwner { get; init; }

    /// <summary>
    /// "{plan}/{node}" from <c>BOBCAT_PLAN_NODE</c> — the correlation that lets a coordination
    /// plan's test-run-gate node link to this run. One more member of the BOBCAT_RUN_ID
    /// family, injected the same way and inherited by a supervisor's workers the same way.
    /// </summary>
    public string? PlanNode { get; init; }

    public static MonitorRunInfo Discover(string mode)
    {
        var runId = Guid.TryParse(Environment.GetEnvironmentVariable(RunIdVariable), out var id)
            ? id
            : Guid.NewGuid();

        var suite = Assembly.GetEntryAssembly()?.GetName().Name ?? "bobcat";

        var (repository, branch) = GitInfo.Discover(Directory.GetCurrentDirectory());

        return new MonitorRunInfo(runId, suite, repository ?? Directory.GetCurrentDirectory(), branch, mode)
        {
            HasExternalOwner =
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RunOwnerVariable)),
            PlanNode = Environment.GetEnvironmentVariable(PlanNodeVariable) is { Length: > 0 } planNode
                ? planNode
                : null
        };
    }
}

/// <summary>
/// Best-effort git metadata from plain file reads — no process spawn, never throws. The
/// repository root is the dashboard's grouping key for parallel suites on one box, so "close
/// enough, cheaply" beats "exact, via git invocation".
/// </summary>
internal static class GitInfo
{
    public static (string? Repository, string? Branch) Discover(string startDirectory)
    {
        try
        {
            var dir = new DirectoryInfo(startDirectory);
            while (dir != null)
            {
                var gitPath = Path.Combine(dir.FullName, ".git");

                if (Directory.Exists(gitPath))
                {
                    return (dir.FullName, readBranch(Path.Combine(gitPath, "HEAD")));
                }

                if (File.Exists(gitPath))
                {
                    // A worktree: ".git" is a file pointing at the real git directory.
                    var text = File.ReadAllText(gitPath).Trim();
                    const string prefix = "gitdir:";
                    if (text.StartsWith(prefix))
                    {
                        var gitDir = text.Substring(prefix.Length).Trim();
                        if (!Path.IsPathRooted(gitDir))
                        {
                            gitDir = Path.GetFullPath(Path.Combine(dir.FullName, gitDir));
                        }

                        return (dir.FullName, readBranch(Path.Combine(gitDir, "HEAD")));
                    }

                    return (dir.FullName, null);
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // Metadata discovery is decoration, never a failure.
        }

        return (null, null);
    }

    private static string? readBranch(string headPath)
    {
        try
        {
            if (!File.Exists(headPath)) return null;

            var head = File.ReadAllText(headPath).Trim();
            const string refPrefix = "ref: refs/heads/";
            if (head.StartsWith(refPrefix)) return head.Substring(refPrefix.Length);

            // Detached HEAD: report the short sha rather than nothing.
            return head.Length >= 8 ? head.Substring(0, 8) : head;
        }
        catch
        {
            return null;
        }
    }
}
