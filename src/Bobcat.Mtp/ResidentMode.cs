using System.Runtime.InteropServices;
using Bobcat.Residency;
using Bobcat.Runtime;

namespace Bobcat.Mtp;

/// <summary>
/// Turns a Bobcat spec host into a resident runner when it is asked to be one (issue #390):
/// <c>MySpecs --resident</c>, or <c>BOBCAT_RESIDENT=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Checked before the test platform is built, not registered as one of its options.</b>
/// Resident mode is not a way of running tests — it never executes a platform request at all — so
/// a platform host that had already been built and then abandoned would be ceremony. Checking the
/// argument first also means the one entry point every consumer already has
/// (<c>BobcatTestApplication.Run</c>, hand-written or generated) is the entry point for this too:
/// <c>dotnet watch --no-hot-reload run -- --resident</c> works with no change to a spec project.
/// </para>
/// <para>
/// <b>Exit code 0 on a restart, and on an orderly shutdown.</b> A resident runner's exit says
/// nothing about any test: the verdicts went out on the ingest stream as they happened. A parent
/// deciding whether to relaunch should not have to tell a red suite from a crashed runner.
/// </para>
/// </remarks>
public static class ResidentMode
{
    /// <summary>The argument that asks a host to stay resident instead of running its specs.</summary>
    public const string Option = "--resident";

    /// <summary>
    /// The same request as an environment variable, for a parent that cannot add an argument —
    /// a <c>dotnet watch</c> profile, or a container's entry point.
    /// </summary>
    public const string Variable = "BOBCAT_RESIDENT";

    /// <summary>Whether this process was asked to be a resident runner.</summary>
    public static bool Requested(IReadOnlyList<string> arguments)
        => arguments.Any(argument => argument.Equals(Option, StringComparison.OrdinalIgnoreCase))
           || Environment.GetEnvironmentVariable(Variable)?.ToLowerInvariant() is "1" or "on" or "true";

    /// <summary>
    /// Register with the monitor and take commands until the process is asked to stop — by a
    /// <c>restart</c> command, or by Ctrl+C / SIGTERM.
    /// </summary>
    public static async Task<int> Run(Action<BobcatRunner> configure, ResidentRunnerOptions? options = null)
    {
        using var stopping = new CancellationTokenSource();

        // SIGTERM is how dotnet watch and a container ask for a shutdown; SIGINT is Ctrl+C. Both
        // are an orderly stop here, and both are handled through PosixSignalRegistration rather
        // than through ProcessExit — a ProcessExit handler runs after this method has returned and
        // disposed the source it would cancel, which aborts the process with SIGABRT on the way
        // out. Exit code 134 instead of 0, from a runner that had done everything right.
        using var interrupt = register(PosixSignal.SIGINT, stopping);
        using var terminate = register(PosixSignal.SIGTERM, stopping);
        using var quit = register(PosixSignal.SIGQUIT, stopping);

        var suite = new BobcatResidentSuite(configure);

        await using var runner = new ResidentRunner(
            suite,
            options ?? new ResidentRunnerOptions { Log = Console.WriteLine });

        Console.WriteLine(
            $"Bobcat resident runner {runner.RunnerId} — {suite.Suite} ({suite.Lane}), "
            + $"{suite.SpecIdentities.Count} specification(s). Waiting for commands.");

        await runner.Run(stopping.Token);

        return 0;
    }

    private static PosixSignalRegistration register(PosixSignal signal, CancellationTokenSource stopping)
        => PosixSignalRegistration.Create(signal, context =>
        {
            // Handled: the runner stops its own loop and returns through Main, rather than the
            // runtime tearing the process down mid-run.
            context.Cancel = true;

            try { stopping.Cancel(); }
            catch (ObjectDisposedException) { /* already shutting down */ }
        });
}
