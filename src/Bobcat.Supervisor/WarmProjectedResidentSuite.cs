using Bobcat.Monitoring;
using Bobcat.Residency;
using Bobcat.Runtime;

namespace Bobcat.Supervisor;

/// <summary>
/// A projected suite a resident runner can run <b>warm</b> (issue #402 item 4): the suite's own
/// test host is held open in Microsoft.Testing.Platform's server mode, and a warm command becomes
/// one <c>testing/runTests</c> request into the live process instead of a fresh launch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cold is delegated, not reimplemented.</b> <see cref="OutOfProcessResidentSuite"/> already
/// does cold correctly and is tested; this wraps it and adds the warm path. So a cold command is
/// byte-for-byte what it always was — a fresh child with the pruned environment — and the only new
/// behaviour is the one a console has to ask for by name.
/// </para>
/// <para>
/// <b>It lives in <c>Bobcat.Supervisor</c> rather than in core, and that is a layering decision
/// (2026-10-06).</b> Warm means holding a live <see cref="IWorkerClient"/>, and the MTP JSON-RPC
/// client is here. Moving that client down into core would tax <em>every</em> spec project — core
/// is what they all reference — for a capability only the resident tool uses. And
/// <see cref="IWorkerClient"/> is documented as THE seam for exactly this: #402 anticipated "a
/// second caller of <c>IWorkerClient</c> rather than a new transport", so the second caller
/// belongs on the supervisor's side of it. <c>Bobcat.Console</c> depending on
/// <c>Bobcat.Supervisor</c> is honest — driving a test host as a process is that tool's whole job
/// in this lane.
/// </para>
/// <para>
/// <b>What warmth actually buys here, and what it does not.</b> It keeps the CLR warm: the JIT,
/// the loaded assemblies, the codegen. It does <em>not</em> keep the suite's own fixtures up —
/// xUnit's assembly and collection fixtures are created and disposed within a run request, so a
/// second request pays for them again. That is the opposite of the Gherkin lane, where what warms
/// is Bobcat's own <c>TestResources.StartAll</c>. Issue #394 measured the saving as 72ms → 11ms →
/// 4ms for the same work, which is real but is CLR warmth, not boot warmth.
/// </para>
/// <para>
/// <b>Each command is still its own run on the wire</b>, which is #393's requirement and was the
/// blocker #402 closed: <c>Bobcat.Xunit</c>'s session-lifetime handler opens and closes the run
/// bracket per request, so a viewer cannot tell a warm run from a cold one except by its speed.
/// Without that, two warm commands would have folded into one never-ending run.
/// </para>
/// </remarks>
public sealed class WarmProjectedResidentSuite : IResidentSuite, IAsyncDisposable
{
    private readonly OutOfProcessResidentSuite _cold;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _commandFile;

    private IWorkerClient? _client;
    private Dictionary<string, string>? _uids;
    private string? _warmUnavailable;

    private WarmProjectedResidentSuite(OutOfProcessResidentSuite cold, string commandFile)
    {
        _cold = cold;
        _commandFile = commandFile;
    }

    /// <summary>
    /// Build one over the suite's test host, asking it what it specifies first.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="OutOfProcessResidentSuite.For"/>, so every launch-time refusal it
    /// makes still happens here and still happens at launch: a host that is not built, a suite
    /// listing no specifications, a framework whose filter spelling Bobcat will not guess at.
    /// </remarks>
    public static async Task<WarmProjectedResidentSuite> For(
        string hostPath,
        string? monitorUrl = null,
        Action<string>? log = null,
        string? listingDirectory = null,
        CancellationToken token = default)
    {
        var cold = await OutOfProcessResidentSuite.For(hostPath, monitorUrl, log, listingDirectory, token);

        var directory = listingDirectory ?? Path.Combine(Path.GetTempPath(), "bobcat-runner");
        Directory.CreateDirectory(directory);

        var commandFile = Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(cold.HostPath)}.{Environment.ProcessId}.command");

        return new WarmProjectedResidentSuite(cold, commandFile);
    }

    public string Suite => _cold.Suite;

    public string Lane => _cold.Lane;

    /// <summary>
    /// Cold always; warm until a warm session leaves the host unusable.
    /// </summary>
    /// <remarks>
    /// The withdrawal follows the Gherkin lane's rule (issue #393): the runner drops <c>warm</c>
    /// from the modes it registers, re-registers so the monitor stops offering a button that would
    /// now be refused, and refuses a warm command <em>with the reason</em> — because "warm is not a
    /// mode this runner offers", from a runner that was offering it a minute ago, explains nothing.
    /// <b>Cold is unaffected</b>, deliberately: a cold command starts over from exactly the thing
    /// that poisoned the warm host.
    /// </remarks>
    public IReadOnlyList<string> Modes => _warmUnavailable is null
        ? [RunnerWire.ColdMode, RunnerWire.WarmMode]
        : [RunnerWire.ColdMode];

    public IReadOnlyList<string> SpecIdentities => _cold.SpecIdentities;

    public SpecManifest Manifest => _cold.Manifest;

    public string HostPath => _cold.HostPath;

    public string? WarmUnavailable => _warmUnavailable;

    public async Task Run(string commandId, SpecSelection selection, string mode, CancellationToken token)
    {
        if (!string.Equals(mode, RunnerWire.WarmMode, StringComparison.OrdinalIgnoreCase))
        {
            // A cold command closes the warm session first. They cannot coexist for the same
            // reason they cannot in the Gherkin lane: the booted host holds the port, the database
            // and the queues a second one would ask for, so "fresh everything" has to include
            // tearing down what is up.
            await StopWarmSession();
            await _cold.Run(commandId, selection, mode, token);
            return;
        }

        await _gate.WaitAsync(token);

        try
        {
            await runWarm(commandId, selection, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task runWarm(string commandId, SpecSelection selection, CancellationToken token)
    {
        try
        {
            var client = _client ??= await launch(token);
            var uids = _uids ??= await discover(client, token);

            var requested = selection.NarrowsAnything
                ? selection.Identities.ToList()
                : SpecIdentities.ToList();

            var resolved = new List<string>();

            foreach (var identity in requested)
            {
                // Refuses rather than guesses, the same rule SpecFilterArguments follows. An
                // identity whose uid we cannot name would otherwise be dropped from the subset,
                // and a request for three specs that ran two looks exactly like a pass.
                if (!uids.TryGetValue(identity, out var uid))
                {
                    throw new InvalidOperationException(
                        $"'{identity}' is in this suite's manifest but the test host's discovery "
                        + "reported no test for it, so there is no platform uid to run. The join is "
                        + "the discovery display name against the manifest's TestClass.TestMethod "
                        + "— a mismatch means the two disagree about this test's name.");
                }

                resolved.Add(uid);
            }

            // Written BEFORE the request, which is what makes it race-free: a resident runner runs
            // one command at a time and refuses rather than queues, so nothing else can be writing
            // this file while the request is in flight. The child reads it when its session opens.
            await File.WriteAllTextAsync(_commandFile, commandId, token);

            var result = await client.Run(resolved, token);

            if (result.Fault is { Length: > 0 } fault)
            {
                // The worker died mid-request. Warm is withdrawn rather than retried: the process
                // that held the warmth is gone, and the next warm command would silently relaunch
                // and look slow for no stated reason.
                _warmUnavailable =
                    $"the warm test host faulted and is gone ({fault}). Cold still works, and a "
                    + "restart brings warm back.";

                await StopWarmSession();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _warmUnavailable =
                $"the warm test host could not serve this command ({e.Message}). Cold still works, "
                + "and a restart brings warm back.";

            await StopWarmSession();
            throw;
        }
    }

    private async Task<IWorkerClient> launch(CancellationToken token)
    {
        // The warm child's environment is the cold child's, minus the command id — which is the
        // whole reason BOBCAT_RUN_COMMAND_FILE exists. A child's environment is fixed at launch,
        // so a warm process serving many commands cannot be told the current one that way; the
        // file is, and it WINS over the variable, so the variable is left out rather than set to
        // something that would be wrong after the first command.
        var environment = new Dictionary<string, string>
        {
            // Forced on for the same reason the cold path forces it: a resident runner exists to
            // serve a console, and BOBCAT_MONITOR=0 is what a CI job sets for a whole box.
            ["BOBCAT_MONITOR"] = "1",
            [MonitorRunInfo.RunCommandFileVariable] = _commandFile
        };

        foreach (var (name, value) in OutOfProcessResidentSuite.EnvironmentFor(command: null))
        {
            // EnvironmentFor's nulls mean "remove", and a launch environment is additive, so a
            // null is simply left out — the child inherits this process's, which is what the cold
            // path's removal achieves there.
            if (value is not null && name != MonitorRunInfo.RunCommandFileVariable)
                environment[name] = value;
        }

        if (_cold.MonitorUrl is { Length: > 0 } url) environment[MonitorPublisher.UrlVariable] = url;

        return await MtpWorkerClient.Launch(HostPath, environment, token);
    }

    /// <summary>
    /// Map every specification identity to the platform uid that runs it.
    /// </summary>
    /// <remarks>
    /// <b>The join is one lookup</b>, which issue #394 established: xUnit v3 reports a test's
    /// discovery display name as <c>Namespace.Class.method</c>, which is exactly what the
    /// manifest's <c>TestClass</c> + <c>TestMethod</c> spell. A uid is a better filter than the
    /// cold path's <c>--filter-method</c>, too — a uid cannot match more than one test, while a
    /// method name can.
    /// </remarks>
    private async Task<Dictionary<string, string>> discover(IWorkerClient client, CancellationToken token)
    {
        var discovered = await client.Discover(token);

        var byDisplayName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var test in discovered) byDisplayName[test.DisplayName] = test.Uid;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in Manifest.Specs)
        {
            if (entry.QualifiedTestMethod is not { Length: > 0 } qualified) continue;

            if (byDisplayName.TryGetValue(qualified, out var uid))
            {
                map[entry.Identity] = uid;
                continue;
            }

            // A nested class is `Ns.Outer+Inner` in the manifest, because that is what a
            // framework's class filter takes, while a display name may spell it with a dot.
            // Tried as a fallback rather than instead: the exact match is the one #394 verified.
            if (byDisplayName.TryGetValue(qualified.Replace('+', '.'), out uid)) map[entry.Identity] = uid;
        }

        return map;
    }

    /// <summary>
    /// Tear down the warm session, if one is up. Idempotent, and never throws — a teardown that
    /// threw would turn a finished run into a failed one.
    /// </summary>
    public async Task StopWarmSession()
    {
        var client = Interlocked.Exchange(ref _client, null);
        _uids = null;

        if (client is null) return;

        try
        {
            await client.DisposeAsync();
        }
        catch
        {
            // The process may already be gone, which is the ordinary case after a fault.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopWarmSession();
        _gate.Dispose();

        try { if (File.Exists(_commandFile)) File.Delete(_commandFile); }
        catch { /* a leftover file in a temp directory is not worth a failure */ }
    }
}
