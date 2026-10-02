using System.Runtime.InteropServices;
using Bobcat.Runtime;

namespace Bobcat.Residency;

/// <summary>
/// Turns a Bobcat spec host into a resident runner when it is asked to be one (issue #390):
/// <c>MySpecs --resident</c>, or <c>BOBCAT_RESIDENT=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Checked before anything else is built, not registered as an option of whatever parser the
/// host uses.</b> Resident mode is not a way of running tests — it never executes a platform
/// request or a command at all — so a host that had been built and then abandoned would be
/// ceremony. Checking the argument first also means the entry point a consumer already has is the
/// entry point for this too: <c>dotnet watch --no-hot-reload run -- --resident</c> works with no
/// change to a spec project.
/// </para>
/// <para>
/// <b>It lives in core, and both Gherkin entry points check it</b> (issue #398):
/// <c>BobcatTestApplication.Run</c> (the MTP host) and <c>BobcatRunner.Run</c> (the JasperFx
/// command family). It was in <c>Bobcat.Mtp</c> when there was only one, but nothing in it was
/// ever about the test platform, and a suite written against the runner before the MTP host
/// existed — Stoat's own specs among them — could not be driven from a console's run buttons.
/// </para>
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

        await suite.DisposeAsync();

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
