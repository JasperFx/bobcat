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
    public const string RunTagVariable = "BOBCAT_RUN_TAG";

    /// <summary>
    /// The monitor command that asked for this run, from <c>BOBCAT_RUN_COMMAND</c> (issue #392).
    /// A <c>BOBCAT_*</c> variable, unlike <see cref="SessionVariable"/>, because Bobcat really is
    /// the thing asking for it: the resident runner sets it per command, and a cold command that
    /// launches a child test host passes it down the same way <see cref="RunIdVariable"/> travels.
    /// </summary>
    public const string RunCommandVariable = "BOBCAT_RUN_COMMAND";

    /// <summary>
    /// The agent session id, which Claude Code puts in the environment of everything it launches.
    /// Deliberately NOT a <c>BOBCAT_*</c> variable: Bobcat does not ask for this one, it reads what
    /// is already there, so a run launched from a session is attributable with no configuration.
    /// </summary>
    public const string SessionVariable = "CLAUDE_CODE_SESSION_ID";

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
    /// An opaque correlation tag from <c>BOBCAT_RUN_TAG</c> — whatever launched the run stamps
    /// a string here (a ticket id, a build number, an external tool's node id) and finds the
    /// run by it later. Bobcat passes it through verbatim and never interprets it: the meaning
    /// belongs entirely to whoever set it. One more member of the BOBCAT_RUN_ID family,
    /// injected the same way and inherited by a supervisor's workers the same way.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// The agent session that launched this run, from <see cref="SessionVariable"/> — opaque and
    /// uninterpreted, like <see cref="Tag"/>, and travelling on <c>run_started</c> beside it.
    /// </summary>
    /// <remarks>
    /// A viewer can then attach a run to the agent that ran it exactly, instead of inferring it: a
    /// plan node tag only covers runs something thought to tag, and matching the run's repository
    /// path against a session's working tree is ambiguous exactly when two agents share a checkout —
    /// the case most worth seeing. Inherited by a supervisor's workers for free, because it is in
    /// the environment they are launched with; only the bracket owner publishes
    /// <c>run_started</c> anyway (see <see cref="HasExternalOwner"/>).
    /// </remarks>
    public string? Session { get; init; }

    /// <summary>
    /// The command this run was started to satisfy, from <see cref="RunCommandVariable"/> — the
    /// resident runner's command id (issue #392), opaque and uninterpreted exactly like
    /// <see cref="Tag"/> and <see cref="Session"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a third independent string and not a reuse of <see cref="Tag"/>, because the two
    /// answer different questions and a command's run routinely needs both: the tag says what work
    /// the run speaks for (a plan node), the command says which button press produced it. A slice's
    /// specs re-run from a console should still be attributed to that slice's node.
    /// </para>
    /// <para>
    /// Null when no command asked — which is every run started any other way, so an ordinary run is
    /// unaffected.
    /// </para>
    /// </remarks>
    public string? Command { get; init; }

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
            Tag = Environment.GetEnvironmentVariable(RunTagVariable) is { Length: > 0 } tag
                ? tag
                : null,
            Session = Environment.GetEnvironmentVariable(SessionVariable) is { Length: > 0 } session
                ? session
                : null,
            Command = Environment.GetEnvironmentVariable(RunCommandVariable) is { Length: > 0 } command
                ? command
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
