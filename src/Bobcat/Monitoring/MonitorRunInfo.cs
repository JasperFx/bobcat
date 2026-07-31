using System.Reflection;

namespace Bobcat.Monitoring;

/// <summary>
/// Identity and metadata for one run as the monitor sees it. RunId honours
/// <c>BOBCAT_RUN_ID</c> when set — that is how a future supervisor groups its worker
/// processes' step streams under one run without any supervisor-side changes here.
/// </summary>
public record MonitorRunInfo(Guid RunId, string Suite, string Repository, string? Branch, string Mode)
{
    public const string RunIdVariable = "BOBCAT_RUN_ID";

    public static MonitorRunInfo Discover(string mode)
    {
        var runId = Guid.TryParse(Environment.GetEnvironmentVariable(RunIdVariable), out var id)
            ? id
            : Guid.NewGuid();

        var suite = Assembly.GetEntryAssembly()?.GetName().Name ?? "bobcat";

        var (repository, branch) = GitInfo.Discover(Directory.GetCurrentDirectory());

        return new MonitorRunInfo(runId, suite, repository ?? Directory.GetCurrentDirectory(), branch, mode);
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
