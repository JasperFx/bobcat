using System.Diagnostics;
using Bobcat.Monitoring;
using Bobcat.Runtime;

namespace Bobcat.Residency;

/// <summary>
/// A suite a resident runner drives from <b>outside its process</b> (issue #399): the runner holds
/// the suite's manifest and runs a command by launching the suite's own test host, narrowed by
/// that framework's filter. This is the only way the projected lane can be resident at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>For a projected suite it has to be a separate process, and that is the lane's defining
/// fact.</b> A Gherkin suite's entry point is Bobcat's, so <c>--resident</c> turns that host into
/// a runner (<see cref="BobcatResidentSuite"/>). A projected suite's entry point belongs to its
/// test framework, which has no idea what <c>--resident</c> means and will not be taught — so the
/// runner lives outside. "A resident runner is not the suite" is a design note in the Gherkin
/// lane; here it is a constraint.
/// </para>
/// <para>
/// <b>Named for the mechanism rather than the lane, because it is lane-neutral — and that is
/// issue #391's whole point.</b> Everything here is a function of the suite's manifest:
/// <c>SpecFilterArguments</c> switches on the <i>framework</i>, so pointing this at a Gherkin host
/// also works, and gets <c>--filter-uid</c>. Doing that is nonetheless the wrong choice for a
/// Gherkin suite, for one reason worth saying out loud: the in-process runner can offer warm
/// (issue #393) and this cannot, because warmth means holding a booted host and here the host is
/// a child that exits. Pay a process per command only when the lane leaves no alternative.
/// </para>
/// <para>
/// <b>Cold only, and that is measured rather than assumed.</b> Issue #394 found that Microsoft's
/// testing platform really does take repeated run requests in one live process on 1.9.1, so warmth
/// is not blocked by the platform — it is blocked by Bobcat's own run bracket, which a projected
/// suite opens on its first scenario and closes from a <c>ProcessExit</c> handler. Two run
/// requests in one process therefore yield one <c>run_started</c>, one run id and no
/// <c>run_finished</c>, with the second command's scenarios landing on the first command's card.
/// See <c>docs/warm-projected-runs.md</c>; until that bracket is per-request, offering warm here
/// would publish a run nobody can read.
/// </para>
/// <para>
/// <b>Every command is one child process, and the environment it inherits is pruned on purpose.</b>
/// A resident runner is long-lived and routinely launched by a parent that sets the
/// <c>BOBCAT_*</c> family; inherited wholesale, three of those variables quietly break the run
/// this suite exists to produce. See <see cref="EnvironmentFor"/>.
/// </para>
/// <para>
/// <b>The identities are read once, at construction</b> — the same contract as the Gherkin lane,
/// for the same reason: a suite that gained a specification since the runner started has a
/// registration the monitor is already holding, and <c>restart</c> is how a parent replaces both.
/// A framework whose filter spelling Bobcat cannot verify is refused here rather than at the first
/// button press, so a person learns at launch.
/// </para>
/// </remarks>
public sealed class OutOfProcessResidentSuite : IResidentSuite
{
    private readonly string _host;
    private readonly SpecManifest _manifest;

    /// <param name="hostPath">
    /// The spec project's test host executable — the thing a person would run to run the suite.
    /// </param>
    /// <param name="manifest">What that host said it specifies, from <see cref="Listing"/>.</param>
    private OutOfProcessResidentSuite(string hostPath, SpecManifest manifest)
    {
        _host = hostPath;
        _manifest = manifest;

        if (manifest.Specs.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{manifest.Suite}' lists no specifications, so there is nothing a monitor could "
                + "ask this runner to run. In the projected lane a test becomes a specification by "
                + "carrying [BobcatScenario] through Bobcat.Xunit or Bobcat.TUnit — a suite listing "
                + "none is usually a spec project with no runner adapter referenced.");
        }

        // Refused at construction, not per command. SpecFilterArguments throws for a framework
        // whose filter spelling is unverified (TUnit today), and a runner that registered its
        // specs and then refused every single command would look broken rather than unsupported.
        SpecFilterArguments.For(manifest, SpecSelection.Of(manifest.Identities[0]));
    }

    /// <summary>
    /// Ask the host what it specifies, then build a suite over it.
    /// </summary>
    /// <remarks>
    /// Two launches is the shape the platform forces: <c>--list-tests</c> with
    /// <c>BOBCAT_LIST_SPECS</c> set is the one request that reaches the generated module
    /// initializer — the only code Bobcat owns in that process — without executing a test.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The host is not where it was said to be, would not list, or wrote no manifest. All three
    /// are launch-time mistakes, and all three are better as a refusal to start than as a runner
    /// that registers nothing and sits there.
    /// </exception>
    /// <param name="hostPath">The spec project's test host executable.</param>
    /// <param name="monitorUrl">See <see cref="MonitorUrl"/>.</param>
    /// <param name="log">See <see cref="Log"/>.</param>
    /// <param name="listingDirectory">Where the listing is written; a temp directory by default.</param>
    public static async Task<OutOfProcessResidentSuite> For(
        string hostPath,
        string? monitorUrl = null,
        Action<string>? log = null,
        string? listingDirectory = null,
        CancellationToken token = default)
    {
        var manifest = await Listing(hostPath, listingDirectory, token);

        return new OutOfProcessResidentSuite(hostPath, manifest)
        {
            MonitorUrl = monitorUrl,
            Log = log
        };
    }

    /// <summary>What <paramref name="hostPath"/> says it specifies.</summary>
    public static async Task<SpecManifest> Listing(
        string hostPath, string? listingDirectory = null, CancellationToken token = default)
    {
        var host = Path.GetFullPath(hostPath);

        if (!File.Exists(host))
        {
            throw new InvalidOperationException(
                $"There is no test host at '{host}'. A projected resident runner is pointed at the "
                + "spec project's own executable — the thing you would run to run the suite — so a "
                + "path that is not there is a launch mistake, not a suite with no specifications.");
        }

        var directory = listingDirectory ?? Path.Combine(Path.GetTempPath(), "bobcat-runner");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(
            directory, $"{Path.GetFileNameWithoutExtension(host)}.{Environment.ProcessId}.specs.json");

        try
        {
            var environment = new Dictionary<string, string?>(EnvironmentFor(command: null))
            {
                [SpecManifest.PathVariable] = path,

                // A listing must not reach for a console. It publishes nothing, and probing is the
                // one thing a question about the suite has no business doing.
                ["BOBCAT_MONITOR"] = "0"
            };

            var (exitCode, output) = await Launch(host, ["--list-tests"], environment, token);

            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(host)}' wrote no spec manifest when asked to list "
                    + $"(exit {exitCode}). A projected suite writes one from the registration its "
                    + "generator emits, so a host that writes none is either not a Bobcat-projected "
                    + $"suite or did not get as far as loading. It said:{Environment.NewLine}{output}");
            }

            return SpecManifest.Read(path);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* a leftover listing in a temp directory is not worth a failure */ }
        }
    }

    public string Suite => _manifest.Suite;

    public string Lane => _manifest.Lane;

    /// <summary>Cold, and only cold. See the remarks on this class and issue #394.</summary>
    public IReadOnlyList<string> Modes => [RunnerWire.ColdMode];

    public IReadOnlyList<string> SpecIdentities
        => _manifest.Identities.OrderBy(identity => identity, SpecIdentity.Comparer).ToList();

    /// <summary>The host this runner speaks for, as an absolute path.</summary>
    public string HostPath => Path.GetFullPath(_host);

    /// <summary>What the host said it specifies — kept so a caller can see the bindings.</summary>
    public SpecManifest Manifest => _manifest;

    /// <summary>Where the runner narrates each launch. Nothing by default.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// The monitor origin a commanded child publishes to. Null leaves the child to resolve its own,
    /// which is right when nobody pointed the runner anywhere either.
    /// </summary>
    /// <remarks>
    /// <b>It has to travel, and leaving it out was a real bug.</b> The runner registers with
    /// whatever <c>ResidentRunnerOptions.Url</c> names, while the child resolves
    /// <c>BOBCAT_MONITOR_URL</c> for itself — so a runner pointed at a second console with
    /// <c>--url</c> would take that console's commands and publish the runs to the default one.
    /// A commanded run has to land on the console that asked for it.
    /// </remarks>
    public string? MonitorUrl { get; init; }

    /// <summary>
    /// Launch the suite's host, narrowed to the selection, and wait for it.
    /// </summary>
    /// <remarks>
    /// A red run is not an error here: the child publishes its own verdicts, and its non-zero exit
    /// code is the outcome rather than a failure of the runner. Throwing means the host could not
    /// be run at all — which <see cref="ResidentRunner"/> reports without dying of it.
    /// </remarks>
    public async Task Run(string commandId, SpecSelection selection, string mode, CancellationToken token)
    {
        var arguments = SpecFilterArguments.For(_manifest, selection);

        var environment = new Dictionary<string, string?>(EnvironmentFor(commandId))
        {
            // Forced on, not inherited. A resident runner exists to serve a console, so a
            // commanded run that published nothing would be pointless — and BOBCAT_MONITOR=0 is
            // exactly the kind of thing a CI job or a shell profile sets for the whole box.
            ["BOBCAT_MONITOR"] = "1"
        };

        if (MonitorUrl is { Length: > 0 } url) environment["BOBCAT_MONITOR_URL"] = url;

        var (exitCode, output) = await Launch(HostPath, arguments, environment, token);

        Log?.Invoke(
            $"{Path.GetFileName(HostPath)} exited {exitCode} for command {commandId}"
            + (output.Length > 0 ? Environment.NewLine + output : ""));
    }

    /// <summary>
    /// The environment one commanded child gets, as a set of <i>overrides</i> on this process's own
    /// — null meaning "remove this one".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pruning is the whole of this method's reason to exist. A resident runner is long-lived
    /// and launched by a parent that may well have set the <c>BOBCAT_*</c> family for its own
    /// reasons, and three of those variables inherited wholesale would break the runs:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>BOBCAT_RUN_ID</c> pins a run's identity, so every command this runner ever serves would
    /// publish under one run id and collapse into a single ever-growing card.
    /// </item>
    /// <item>
    /// <c>BOBCAT_RUN_OWNER</c> says somebody else owns the run bracket, so no child would publish
    /// <c>run_started</c> or <c>run_finished</c> at all and every commanded run would be invisible.
    /// </item>
    /// <item>
    /// <c>BOBCAT_LIST_SPECS</c> turns the launch into a listing's subject, rewriting a manifest
    /// the runner is not asking for.
    /// </item>
    /// <item>
    /// <c>BOBCAT_RESIDENT</c> asks a host to <i>become a runner</i>. No projected host reads it
    /// today, which is exactly why it is cleared here rather than relied upon: a child that one
    /// day did read it would register itself with the monitor and never run the command.
    /// </item>
    /// <item>
    /// <c>BOBCAT_RUN_COMMAND_FILE</c> (issue #402) names a file whose contents are the CURRENT
    /// request's command id, and it WINS over <c>BOBCAT_RUN_COMMAND</c>. A cold child has no such
    /// request, so a parent's stale file must never be read as this command — it would overwrite
    /// the correct id with whatever some other runner last wrote. The warm path sets it
    /// deliberately, per request.
    /// </item>
    /// </list>
    /// <para>
    /// What is <i>added</i> is one variable: <c>BOBCAT_RUN_COMMAND</c>, so the run the child
    /// publishes carries the button press that caused it (issue #392). This is the case that
    /// variable was built for — the Gherkin lane sets the command in process instead, because
    /// there the runner and the run share one. It also suppresses the agent session on the child's
    /// <c>run_started</c> (issue #401) for free, by the one rule that governs both lanes.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string?> EnvironmentFor(string? command)
        => new Dictionary<string, string?>
        {
            [MonitorRunInfo.RunIdVariable] = null,
            [MonitorRunInfo.RunOwnerVariable] = null,
            [SpecManifest.PathVariable] = null,
            [ResidentMode.Variable] = null,
            [MonitorRunInfo.RunCommandFileVariable] = null,
            [MonitorRunInfo.RunCommandVariable] = command,
            ["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        };

    /// <summary>
    /// Run the host to completion, returning its exit code and whatever it wrote.
    /// </summary>
    /// <remarks>
    /// Cancellation kills the process tree rather than waiting for it, because the only thing that
    /// cancels a commanded run is a <c>restart</c> — and a wedged run is the main reason someone
    /// restarts a runner, so a restart that waited would be useless in exactly the case it exists
    /// for.
    /// </remarks>
    private static async Task<(int ExitCode, string Output)> Launch(
        string host,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken token)
    {
        var info = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(host)!
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        foreach (var (name, value) in environment)
        {
            if (value is null) info.Environment.Remove(name);
            else info.Environment[name] = value;
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"'{host}' could not be started.");

        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }

            throw;
        }

        var output = (await stdout).TrimEnd() + Environment.NewLine + (await stderr).TrimEnd();

        return (process.ExitCode, output.Trim());
    }
}
