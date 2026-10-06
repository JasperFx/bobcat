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
    /// A file whose contents are the command id for <em>this</em> request, from
    /// <c>BOBCAT_RUN_COMMAND_FILE</c> (issue #402). It wins over
    /// <see cref="RunCommandVariable"/> when both are set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a file, when a variable already carries this.</b> A warm projected suite is a child
    /// process a resident runner holds open across many commands, and <b>a child's environment is
    /// fixed at launch</b>. So <c>BOBCAT_RUN_COMMAND</c> cannot serve: every command after the
    /// first would be stamped with the first one's id, which is precisely the mis-attribution
    /// issue #401 was opened to fix — a run reported as "started by" something that did not ask
    /// for it.
    /// </para>
    /// <para>
    /// Nor is there a slot in the protocol. Measured on Microsoft.Testing.Platform 1.9.1: the
    /// <c>runId</c> a client sends on <c>testing/runTests</c> is the client's own and is <b>not</b>
    /// the <c>SessionUid</c> the session handler receives — they are unrelated GUIDs. A file is
    /// the channel that is left.
    /// </para>
    /// <para>
    /// <b>It is the inverse of <c>BOBCAT_LIST_SPECS</c></b>, and deliberately so: there a path is
    /// fixed at launch and the <em>child</em> writes what the parent reads; here a path is fixed at
    /// launch and the <em>parent</em> writes what the child reads. The race does not arise, because
    /// a resident runner runs one command at a time and refuses rather than queues — the write
    /// happens before the request is sent.
    /// </para>
    /// <para>
    /// A missing or empty file means no command, not an error: a cold child never has one, and
    /// anything that cannot be read falls back to the variable and then to null. This is run
    /// attribution, and it must never be able to fail a run.
    /// </para>
    /// </remarks>
    public const string RunCommandFileVariable = "BOBCAT_RUN_COMMAND_FILE";

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
    /// <b>Null whenever <see cref="Command"/> is set</b>, however the session was discovered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A viewer can attach a run to the agent that ran it exactly, instead of inferring it: a
    /// plan node tag only covers runs something thought to tag, and matching the run's repository
    /// path against a session's working tree is ambiguous exactly when two agents share a checkout —
    /// the case most worth seeing. Inherited by a supervisor's workers for free, because it is in
    /// the environment they are launched with; only the bracket owner publishes
    /// <c>run_started</c> anyway (see <see cref="HasExternalOwner"/>).
    /// </para>
    /// <para>
    /// <b>A commanded run has no session, and that is one rule rather than a flag</b> (issue #401).
    /// A resident runner started from an agent's terminal inherits that agent's
    /// <see cref="SessionVariable"/> and holds it for its whole life, so every run it makes for a
    /// monitor command would otherwise be stamped with the session that launched the <i>runner</i>
    /// — and a viewer would say "started by this session" about runs a person pressed in the UI.
    /// <see cref="Command"/> is the true answer to who asked, so its presence is what suppresses
    /// the session. Written as a getter that consults <see cref="Command"/> rather than as
    /// something a caller remembers to clear, because a <c>with { Command = … }</c> anywhere has
    /// to honour it — the resident suite sets the command on the runner, and
    /// <c>BOBCAT_RUN_COMMAND</c> carries it into a child host, and both are commanded runs.
    /// </para>
    /// </remarks>
    public string? Session
    {
        get => Command is { Length: > 0 } ? null : _session;
        init => _session = value;
    }

    private readonly string? _session;

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
            Command = commandForThisRun()
        };
    }

    /// <summary>
    /// The command this run is serving: the per-request file first, then the process-wide variable.
    /// </summary>
    /// <remarks>
    /// The file wins because it is the narrower claim. A warm child inherits
    /// <c>BOBCAT_RUN_COMMAND</c> once at launch and keeps it for life, so a process serving several
    /// commands would otherwise report them all as the first — see
    /// <see cref="RunCommandFileVariable"/>. Every failure reads as "no command": this is run
    /// attribution, and it must not be able to fail a run.
    /// </remarks>
    private static string? commandForThisRun()
    {
        var path = Environment.GetEnvironmentVariable(RunCommandFileVariable);

        if (path is { Length: > 0 })
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } fromFile)
                    return fromFile;
            }
            catch
            {
                // Unreadable, locked, vanished between the two calls — fall through to the
                // variable. A run is never failed by its own attribution.
            }
        }

        return Environment.GetEnvironmentVariable(RunCommandVariable) is { Length: > 0 } command
            ? command
            : null;
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
