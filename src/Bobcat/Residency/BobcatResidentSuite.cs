using System.Reflection;
using Bobcat.Runtime;

namespace Bobcat.Residency;

/// <summary>
/// The Gherkin lane as a <see cref="IResidentSuite"/> (issue #390): a Bobcat spec project running
/// monitor commands in its own process, cold or warm.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cold means a fresh <see cref="BobcatRunner"/> per command</b>, configured by the same
/// callback an MTP host uses — so a command gets the full <c>StartAll</c> →
/// <c>ResetAll</c>/<c>BeginScenarioAll</c> bracket → teardown that an ordinary run gets, and a
/// second command cannot see the first's state because none of it survived.
/// </para>
/// <para>
/// <b>Warm (issue #393) keeps one booted runner between commands</b>, so a re-run skips the boot.
/// It is a Gherkin-lane ability because Bobcat owns this process; a projected suite's test
/// framework owns its own, which is why that lane is cold-only (issue #394). What warmth buys is
/// only who pays for <c>StartAll</c> — every selected scenario still gets the full per-scenario
/// reset bracket, so warm never means dirty.
/// </para>
/// <para>
/// <b>A cold command closes the warm session first.</b> They cannot coexist: a booted host is
/// holding the port, the database and the queues a second one would try to take, so "fresh
/// everything" has to include tearing down what is already up. Cold is therefore exactly as cold
/// as it claims, at the price of a boot the next warm command pays again.
/// </para>
/// <para>
/// <b>The identities are read once, at construction.</b> That is not a cache to be invalidated: a
/// source change restarts the runner (it lives under <c>dotnet watch --no-hot-reload</c>), so the
/// list describes exactly the code this process was built from, which is the code a command will
/// run against.
/// </para>
/// </remarks>
public sealed class BobcatResidentSuite : IResidentSuite, IAsyncDisposable
{
    /// <summary>What a resident run reports itself as to the monitor.</summary>
    public const string Mode = "resident";

    private readonly Action<BobcatRunner> _configure;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BobcatRunner? _warm;
    private string? _unusable;

    /// <param name="configure">
    /// Registers features and resources on a runner — the same callback
    /// <c>BobcatTestApplication.Run</c> takes, and under the same contract: it is called once per
    /// cold command and once per warm session, so it must be safe to call repeatedly and must not
    /// itself do expensive work.
    /// </param>
    public BobcatResidentSuite(Action<BobcatRunner> configure)
    {
        _configure = configure;

        var listing = build();
        Suite = listing.Manifest().Suite;
        SpecIdentities = listing.SpecIdentities
            .OrderBy(identity => identity, SpecIdentity.Comparer)
            .ToList();
    }

    public string Suite { get; }

    public string Lane => SpecManifest.GherkinLane;

    /// <summary>
    /// Whether this suite offers warm mode at all. On by default for the Gherkin lane, which can
    /// honour it; a consumer whose resources genuinely cannot be shared between commands turns it
    /// off and every command is cold.
    /// </summary>
    public bool OffersWarm { get; init; } = true;

    public IReadOnlyList<string> Modes
        => OffersWarm && _unusable is null
            ? [RunnerWire.ColdMode, RunnerWire.WarmMode]
            : [RunnerWire.ColdMode];

    public IReadOnlyList<string> SpecIdentities { get; }

    /// <summary>
    /// Why warm mode is off the table, or null. Set when a warm selection leaves the host
    /// damaged — a reset that threw, a teardown that blew up — and never cleared: a warm session
    /// that broke is broken, and a person who wants a working one asks for a new runner.
    /// </summary>
    /// <remarks>
    /// Cold is unaffected, deliberately. A cold command builds a new runner over fresh resources,
    /// so the thing that poisoned the warm host is exactly what cold starts over from.
    /// </remarks>
    public string? WarmUnavailable => _unusable;

    /// <summary>Whether a booted host is being held between commands right now.</summary>
    public bool IsWarm => _warm is not null;

    /// <summary>Whether the console output of each commanded run is suppressed.</summary>
    /// <remarks>
    /// Off by default: a resident runner usually has a terminal, and a person watching it wants to
    /// see what a command did without opening the console.
    /// </remarks>
    public bool Quiet { get; init; }

    public async Task Run(string commandId, SpecSelection selection, string mode, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (mode == RunnerWire.WarmMode && OffersWarm && _unusable is null)
            {
                await runWarm(commandId, selection, token);
                return;
            }

            await runCold(commandId, selection);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task runCold(string commandId, SpecSelection selection)
    {
        // Fresh everything, which has to include tearing down a warm session that is holding the
        // resources a new one would ask for.
        await closeWarm();

        var runner = build();

        runner.NarrowTo(selection);
        runner.PublishToMonitor = true;
        runner.MonitorMode = Mode;
        runner.MonitorCommand = commandId;

        // RunAll never throws for a harness failure (issue #123) — a resource that would not start
        // comes back as a catastrophic SuiteResults, which is a report and not the runner's death.
        await runner.RunAll();
    }

    private async Task runWarm(string commandId, SpecSelection selection, CancellationToken token)
    {
        if (_warm is null)
        {
            var booting = build();
            booting.PublishToMonitor = true;
            booting.MonitorMode = Mode;

            var failure = await booting.StartWarmSuite();
            if (failure is not null)
            {
                // StartWarmSuite tore down whatever it had started, so there is nothing warm to
                // hold. Warm is withdrawn rather than retried: a suite whose resources will not
                // come up will not come up on the next command either.
                _unusable = failure;
                throw new InvalidOperationException(
                    $"the warm suite could not be started, so '{commandId}' did not run: {failure}");
            }

            _warm = booting;
        }

        await _warm.RunWarm(selection, commandId);

        if (_warm?.WarmSuiteUnusable is { Length: > 0 } damage)
        {
            _unusable = damage;

            // Torn down now rather than left for the next command: a host nobody will run on
            // again should not keep holding a port.
            await closeWarm();
        }
    }

    private async Task closeWarm()
    {
        var warm = _warm;
        _warm = null;

        if (warm is null) return;

        try
        {
            await warm.StopWarmSuite();
        }
        catch (Exception e)
        {
            // A teardown that fails is news, not the runner's death — and the session is gone
            // either way, which is what the caller needed.
            if (_unusable is null) _unusable = $"the warm suite would not shut down cleanly: {e.Message}";
        }
    }

    private BobcatRunner build()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = Quiet };
        _configure(runner);
        return runner;
    }

    /// <summary>
    /// A suite whose features are already in hand, for a caller that is not going through a
    /// configure callback.
    /// </summary>
    public static BobcatResidentSuite For(Assembly assembly)
        => new(runner => runner.ScanForFeatures(assembly));

    public async ValueTask DisposeAsync()
    {
        await closeWarm();
        _gate.Dispose();
    }
}
