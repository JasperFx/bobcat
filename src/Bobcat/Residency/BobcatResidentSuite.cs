using System.Reflection;
using Bobcat.Runtime;

namespace Bobcat.Residency;

/// <summary>
/// The Gherkin lane as a <see cref="IResidentSuite"/> (issue #390): a Bobcat spec project running
/// monitor commands in its own process, cold.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cold means a fresh <see cref="BobcatRunner"/> per command</b>, configured by the same
/// callback an MTP host uses — so a command gets the full <c>StartAll</c> →
/// <c>ResetAll</c>/<c>BeginScenarioAll</c> bracket → teardown that an ordinary run gets, and a
/// second command cannot see the first's state because none of it survived. Warm, which keeps the
/// resources up between commands, is issue #393.
/// </para>
/// <para>
/// <b>The identities are read once, at construction.</b> That is not a cache to be invalidated: a
/// source change restarts the runner (it lives under <c>dotnet watch --no-hot-reload</c>), so the
/// list describes exactly the code this process was built from, which is the code a command will
/// run against.
/// </para>
/// </remarks>
public sealed class BobcatResidentSuite : IResidentSuite
{
    /// <summary>What a resident run reports itself as to the monitor.</summary>
    public const string Mode = "resident";

    private readonly Action<BobcatRunner> _configure;

    /// <param name="configure">
    /// Registers features and resources on a runner — the same callback
    /// <c>BobcatTestApplication.Run</c> takes, and under the same contract: it is called once per
    /// command, so it must be safe to call repeatedly and must not itself do expensive work.
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

    /// <summary>Cold only, until issue #393.</summary>
    public IReadOnlyList<string> Modes => [RunnerWire.ColdMode];

    public IReadOnlyList<string> SpecIdentities { get; }

    /// <summary>Whether the console output of each commanded run is suppressed.</summary>
    /// <remarks>
    /// Off by default: a resident runner usually has a terminal, and a person watching it wants to
    /// see what a command did without opening the console.
    /// </remarks>
    public bool Quiet { get; init; }

    public async Task Run(string commandId, SpecSelection selection, string mode, CancellationToken token)
    {
        var runner = build();

        runner.NarrowTo(selection);
        runner.PublishToMonitor = true;
        runner.MonitorMode = Mode;
        runner.MonitorCommand = commandId;
        runner.SuppressConsoleOutput = Quiet;

        // RunAll never throws for a harness failure (issue #123) — a resource that would not start
        // comes back as a catastrophic SuiteResults, which is a report and not the runner's death.
        await runner.RunAll();
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
}
