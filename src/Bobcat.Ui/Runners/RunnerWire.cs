using System.Text.Json;
using System.Text.Json.Serialization;
using Wolverine.Attributes;

namespace Bobcat.Ui.Runners;

// The runner wire (stoat#77, settled by the spike in stoat#84): CloudEvents in structured mode,
// each message's [MessageIdentity] its CloudEvents `type`. Bobcat mirrors these by hand — it
// references nothing of Stoat's — so a rename here is a wire break, pinned by the contract tests.

/// <summary>A resident runner says what it is and what it can run. Idempotent; sent again on every
/// reconnect and every restart.</summary>
/// <param name="Repository">The checkout's local path. Stoat resolves it to <c>owner/name</c>.</param>
/// <param name="Lane"><c>gherkin</c> or <c>projected</c>.</param>
/// <param name="Modes">What it can do: <c>cold</c>, and <c>warm</c> where its lane allows.</param>
/// <param name="Specs">Every spec identity it can run, <c>{Feature}/{Scenario}</c>.</param>
/// <param name="Outline">Each spec's planned steps, from Bobcat with bobcat#644: what the runner page
/// shows before anything has run. Null from a runner that predates it.</param>
[MessageIdentity("bobcat.runner.registered")]
public record RunnerRegistered(
    string RunnerId, string Repository, string? Branch, string? Suite, string Lane,
    IReadOnlyList<string>? Modes, IReadOnlyList<string> Specs,
    IReadOnlyList<RunnerSpecOutline>? Outline = null);

/// <summary>One specification's planned steps, each a line (<c>Given a customer</c>), as a runner registers it.</summary>
public record RunnerSpecOutline(string Identity, IReadOnlyList<string> Steps);

/// <summary>The runner took a command, or refused it and said why.</summary>
/// <param name="Reason">The human sentence.</param>
/// <param name="Refusal">What kind of refusal, from Bobcat 0.30.0 (bobcat#400): <c>busy</c> (not
/// now: send it again) or <c>unknown-spec</c> / <c>unsupported-mode</c> / <c>empty</c> (not this).
/// Null from a runner that predates it, and Stoat then reads the reason text.</param>
[MessageIdentity("bobcat.runner.command.acknowledged")]
public record RunnerCommandAcknowledged(string RunnerId, Guid CommandId, bool Accepted, string? Reason = null, string? Refusal = null);

/// <summary>What <c>stoat runner</c> (stoat#85) knows that the runner itself cannot say, because it
/// is not running: that it is rebuilding, or that its build failed.</summary>
[MessageIdentity("stoat.runner.status")]
public record RunnerStatusReported(string RunnerId, string State, string? Detail = null);

/// <summary>Run these spec identities. Out to the runner on its command stream.</summary>
[MessageIdentity("stoat.runner.command.run")]
public record RunSpecsCommand(Guid CommandId, IReadOnlyList<string> Specs, string Mode);

/// <summary>Exit so the parent can relaunch a fresh runner — "force a new runner".</summary>
[MessageIdentity("stoat.runner.command.restart")]
public record RestartRunnerCommand(Guid CommandId);

public static class RunnerWire
{
    public const string ContentType = "application/cloudevents+json";
    public const string Source = "stoat";
    public const string KeepAliveEvent = "keepalive";

    public const string Cold = "cold";
    public const string Warm = "warm";

    public const string Rebuilding = "rebuilding";
    public const string BuildFailed = "build-failed";

    /// <summary><c>stoat runner</c>'s word on a runner that exited on its own, other than to be
    /// restarted: it waits for a file change rather than relaunching into the same failure.</summary>
    public const string Exited = "exited";

    /// <summary>
    /// The exit code a resident runner uses for "relaunch me" after a <c>restart</c> command
    /// (<c>EX_TEMPFAIL</c>). <c>stoat runner</c> relaunches on it and on nothing else; with no parent,
    /// exiting is still the right answer.
    /// </summary>
    public const int RestartExitCode = 75;

    /// <summary>The environment <c>stoat runner</c> hands the runner it launches — the launch contract
    /// with Bobcat's resident mode (JasperFx/bobcat#390).</summary>
    /// <summary>What Bobcat 0.29.0 reads (<c>ResidentMode</c>): <c>--resident</c>, or
    /// <c>BOBCAT_RESIDENT=1</c>. <c>BOBCAT_RUNNER=resident</c> was this side's guess before Bobcat
    /// shipped, and nothing ever read it.</summary>
    public const string ResidentOption = "--resident";
    public const string ResidentVariable = "BOBCAT_RESIDENT";
    public const string RunnerIdVariable = "BOBCAT_RUNNER_ID";
    public const string MonitorUrlVariable = "BOBCAT_MONITOR_URL";

    /// <summary>The agent session Bobcat stamps on <c>run_started</c> — kept away from a resident runner.</summary>
    public const string SessionVariable = "CLAUDE_CODE_SESSION_ID";

    /// <summary>
    /// ⚠️ The ONLY inbound types the wire accepts. Wolverine's CloudEvents mapper resolves a `type`
    /// against every message type the host handles, so without this list an unauthenticated POST
    /// could invoke any Stoat command (stoat#84, <c>CloudEventsWireSpikeTests</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Type> Inbound = new Dictionary<string, Type>
    {
        ["bobcat.runner.registered"] = typeof(RunnerRegistered),
        ["bobcat.runner.command.acknowledged"] = typeof(RunnerCommandAcknowledged),
        ["stoat.runner.status"] = typeof(RunnerStatusReported),
    };

    /// <summary>camelCase like the rest of Stoat, case-insensitive in, and null attributes left off
    /// so Wolverine's unused CloudEvents extensions never reach the wire.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
