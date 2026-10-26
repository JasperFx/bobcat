using System.Runtime.InteropServices;
using Bobcat.Runtime;

namespace Bobcat.Residency;

/// <summary>
/// Runs a resident runner over a suite (issue #390): the <c>bobcat resident</c> tool's loop, driving
/// an <see cref="IResidentSuite"/> until it is asked to stop.
/// </summary>
/// <remarks>
/// <para>
/// <b>A restart exits 75; an orderly stop exits 0</b> (issue #397). Neither says anything about
/// any test — the verdicts went out on the ingest stream as they happened, so a parent deciding
/// whether to relaunch never has to tell a red suite from a crashed runner. But it does have to
/// tell "relaunch me" from "I'm done", and 0 could not: 0 is also what a <i>non-resident</i> host
/// returns after running its whole suite, so a parent that relaunched on 0 would run such a suite
/// in a loop forever. 75 is <c>EX_TEMPFAIL</c>, which is as close as the convention comes to "try
/// again".
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

    /// <summary>
    /// The exit code after a <c>restart</c> command — <c>EX_TEMPFAIL</c>, which a parent reads as
    /// "relaunch me" (issue #397). Distinct from 0 precisely because 0 is what every other
    /// orderly exit returns, resident or not.
    /// </summary>
    public const int RestartExitCode = 75;

    /// <summary>
    /// The argument a parent passes to say it relaunches the runner on <see cref="RestartExitCode"/>
    /// (bobcat#796). Without it, <c>bobcat resident</c> restarts in place, because nothing else would.
    /// </summary>
    public const string SupervisedOption = "--supervised";

    /// <summary>The same declaration as an environment variable, for a parent that cannot add an argument.</summary>
    public const string SupervisedVariable = "BOBCAT_SUPERVISED";

    /// <summary>Whether a parent declared, by environment, that it relaunches the runner on a restart.</summary>
    public static bool SupervisedByEnvironment()
        => Environment.GetEnvironmentVariable(SupervisedVariable)?.ToLowerInvariant() is "1" or "on" or "true";

    /// <summary>Whether this process was asked to be a resident runner.</summary>
    public static bool Requested(IReadOnlyList<string> arguments)
        => arguments.Any(argument => argument.Equals(Option, StringComparison.OrdinalIgnoreCase))
           || Environment.GetEnvironmentVariable(Variable)?.ToLowerInvariant() is "1" or "on" or "true";

    /// <summary>
    /// Run <paramref name="suite"/> as a resident runner, taking commands until the process is asked
    /// to stop — by a <c>restart</c> command, or by Ctrl+C / SIGTERM. The suite is the out-of-process
    /// lane (issue #399): the runner is a different process from the suite.
    /// </summary>
    public static async Task<int> Run(IResidentSuite suite, ResidentRunnerOptions? options = null)
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

        await using var runner = new ResidentRunner(
            suite,
            options ?? new ResidentRunnerOptions { Log = Console.WriteLine });

        Console.WriteLine(
            $"Bobcat resident runner {runner.RunnerId} — {suite.Suite} ({suite.Lane}), "
            + $"{suite.SpecIdentities.Count} specification(s). Waiting for commands.");

        await runner.Run(stopping.Token);

        if (suite is IAsyncDisposable disposable) await disposable.DisposeAsync();

        return runner.RestartRequested ? RestartExitCode : 0;
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
