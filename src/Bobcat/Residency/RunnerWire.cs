using System.Text.Json;

namespace Bobcat.Residency;

/// <summary>
/// The resident runner's wire (issue #390): the CloudEvent types, the routes, and the payload
/// shapes on each side.
/// </summary>
/// <remarks>
/// <para>
/// <b>The runner is a client.</b> It connects out to the monitor and <i>asks</i> for work; the
/// monitor can only answer a runner that asked. That is the whole security model, and it is why
/// there is no listener here and no port to open: a monitor can never make a runner do anything,
/// and a runner that never registers is an idle process.
/// </para>
/// <para>
/// <b>This is a public contract with no assembly reference to warn the other side</b> — the same
/// standing as <c>Bobcat.Monitoring.MonitorEvents</c>. Changing a shape here breaks a console that
/// has nothing to tell it, so additions are trailing and optional and the shapes are pinned by
/// contract tests.
/// </para>
/// </remarks>
public static class RunnerWire
{
    /// <summary>Everything a runner posts goes here, whatever its type.</summary>
    public const string EventsRoute = "/api/runners/events";

    /// <summary>The command stream for one runner, read as <c>text/event-stream</c>.</summary>
    public static string CommandsRoute(string runnerId) => $"/api/runners/{Uri.EscapeDataString(runnerId)}/commands";

    // --- Runner → monitor.

    /// <summary>
    /// "I exist, here is what I can run." Idempotent, and re-sent on every reconnect and every
    /// restart, so a monitor that lost its state recovers without the runner being told to.
    /// </summary>
    public const string RegisteredType = "bobcat.runner.registered";

    /// <summary>"I will run that command" — or will not, and why.</summary>
    public const string AcknowledgedType = "bobcat.runner.command.acknowledged";

    // --- Monitor → runner, on the stream.

    /// <summary>"Run these specifications."</summary>
    public const string RunCommandType = "stoat.runner.command.run";

    /// <summary>"Exit, so your parent can relaunch you."</summary>
    public const string RestartCommandType = "stoat.runner.command.restart";

    /// <summary>The SSE event name a keepalive comes as; ignored.</summary>
    public const string KeepaliveType = "keepalive";

    // --- Modes.

    /// <summary>A fresh filtered run per command. Always offered; always the default.</summary>
    public const string ColdMode = "cold";

    /// <summary>
    /// Command after command inside one booted host (issue #393). Offered only by a lane that can
    /// do it, and opted into per command — never assumed.
    /// </summary>
    public const string WarmMode = "warm";

    /// <summary>
    /// camelCase, and that is the contract: the receiving side is a .NET web host whose default is
    /// camelCase, so the payloads here are written the way it will read them.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Why a command was refused, as one word a monitor can act on (issue #400).
/// </summary>
/// <remarks>
/// <para>
/// <b>The point is that one of the four refusals means something different from the other
/// three.</b> <see cref="Busy"/> means <i>not now</i>: it is the ordinary answer to a command sent
/// the instant <c>run_finished</c> arrives (the in-flight slot clears a hair later), and the right
/// response is to send it again. The other three mean <i>not this</i>, and resending is pointless.
/// </para>
/// <para>
/// <b>It exists because the alternative was a monitor matching on prose.</b> Stoat owns the
/// command queue and has to tell busy apart to know whether to resend; with only
/// <see cref="RunnerAcknowledgement.Reason"/> to go on it matched the sentence, which means a
/// reworded sentence here would quietly turn every busy into a hard rejection a person reads as a
/// failed button. The reason stays the human sentence; this is the machine's half.
/// </para>
/// </remarks>
public static class RunnerRefusal
{
    /// <summary>A command is already in flight. Not now — send it again.</summary>
    public const string Busy = "busy";

    /// <summary>The command named a specification this runner does not have.</summary>
    public const string UnknownSpec = "unknown-spec";

    /// <summary>The command asked for a mode this runner does not offer, or no longer offers.</summary>
    public const string UnsupportedMode = "unsupported-mode";

    /// <summary>The command named no specification at all.</summary>
    public const string Empty = "empty";
}

/// <summary>
/// What a runner says about itself when it registers.
/// </summary>
/// <param name="RunnerId">
/// Stable for the life of this process, and the address of its command stream.
/// </param>
/// <param name="Repository">
/// The checkout this runner speaks for. <b>One runner per checkout</b> is the rule that makes a
/// command mean "run against the code in that worktree" — which is why the repository and branch
/// are declared here rather than being left for a monitor to infer from a run's events.
/// </param>
/// <param name="Branch">The branch that checkout is on, or null when it is not a git worktree.</param>
/// <param name="Suite">The suite's name, the same string <c>run_started</c> reports.</param>
/// <param name="Lane">
/// <c>gherkin</c> or <c>projected</c>. Only the runner knows its lane, and it decides what a
/// command can mean — see issue #391.
/// </param>
/// <param name="Modes">
/// The modes this runner offers. A command naming a mode that is not here is rejected, rather
/// than quietly downgraded: a person who asked for warm and silently got cold would read the
/// resulting wall clock as warm mode not working.
/// </param>
/// <param name="Specs">
/// Every specification identity this runner has. It is both what a monitor offers a person and
/// what lets the runner reject an identity that is not its own.
/// </param>
public sealed record RunnerRegistration(
    string RunnerId,
    string Repository,
    string? Branch,
    string Suite,
    string Lane,
    IReadOnlyList<string> Modes,
    IReadOnlyList<string> Specs);

/// <summary>
/// A runner's answer to one command.
/// </summary>
/// <param name="Accepted">
/// Whether the runner is going to do it. A rejection is always accompanied by a
/// <paramref name="Reason"/>, because the alternative — a command that is simply never answered —
/// is indistinguishable from a runner that died.
/// </param>
/// <param name="Reason">The human sentence, written to be shown to whoever pressed the button.</param>
/// <param name="Refusal">
/// The machine's half of the same answer: one of <see cref="RunnerRefusal"/>'s words, or null on
/// an acceptance (issue #400). Trailing and optional, so a monitor that predates it is unaffected
/// and goes on reading <paramref name="Reason"/>.
/// </param>
public sealed record RunnerAcknowledgement(
    string RunnerId,
    string CommandId,
    bool Accepted,
    string? Reason = null,
    string? Refusal = null);

/// <summary>
/// "Run these specifications" — a command names identities and nothing else.
/// </summary>
/// <remarks>
/// No arguments, no paths, no flags, deliberately. A command is <b>data</b>: everything it can ask
/// for is something the runner already told the monitor it has. That is what makes the channel safe
/// to leave open — the worst a hostile monitor can do is ask for a test run.
/// </remarks>
/// <param name="Mode">
/// <c>cold</c> or <c>warm</c>, and absent means cold. Cold is the default because a fresh filtered
/// run always runs the current code.
/// </param>
public sealed record RunCommand(
    string CommandId,
    IReadOnlyList<string> Specs,
    string? Mode = null)
{
    /// <summary>The mode asked for, with absent read as <see cref="RunnerWire.ColdMode"/>.</summary>
    public string ResolvedMode => string.IsNullOrWhiteSpace(Mode) ? RunnerWire.ColdMode : Mode!;
}

/// <summary>
/// "Exit, so your parent can relaunch you."
/// </summary>
/// <remarks>
/// The runner only ever exits; it never relaunches itself. <c>dotnet watch</c> does not relaunch a
/// process that exited on its own, so the relaunch belongs to whatever started the watch
/// (stoat#85). Without a parent, exiting is still the right answer — a person asked for a new
/// runner and got one fewer stale one.
/// </remarks>
public sealed record RestartCommand(string CommandId);
