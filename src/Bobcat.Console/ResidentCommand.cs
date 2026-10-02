using Bobcat.Residency;
using JasperFx.CommandLine;

namespace Bobcat.Console;

public class ResidentInput
{
    [Description("The spec project's test host executable — the thing you would run to run the suite")]
    public string HostPath { get; set; } = string.Empty;

    [Description("Base URL of the monitor to register with; defaults to the one every publisher probes")]
    [FlagAlias("url", 'u')]
    public string? UrlFlag { get; set; }

    [Description("A stable runner id. Defaults to BOBCAT_RUNNER_ID, then to a fresh GUID")]
    [FlagAlias("id", 'i')]
    public string? IdFlag { get; set; }

    [Description("List what the host specifies and exit, without registering with anything")]
    public bool ListFlag { get; set; }

    /// <summary>
    /// What the process should exit with — 75 after a restart (issue #397), 0 otherwise. Read back
    /// by <c>Program</c>, because JasperFx's true/false cannot carry a third answer.
    /// </summary>
    public int? ExitCode { get; set; }
}

/// <summary>
/// Issue #399 — <c>bobcat resident &lt;host&gt;</c>: a resident runner for a suite whose process
/// Bobcat does not own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the tool and not the suite.</b> A Gherkin suite goes resident by being asked
/// (<c>MySpecs --resident</c>): Bobcat owns that entry point. A projected suite's entry point
/// belongs to xUnit or TUnit, so something outside it has to hold the manifest and launch a
/// filtered host per command — and that something has to ship from here, because the console at
/// the other end references nothing in this repository and so cannot be handed a class to host.
/// The <c>bobcat</c> tool is where a run-side executable with no server in it belongs.
/// </para>
/// <para>
/// <b>Cold only.</b> Issue #394 measured what warmth would take in this lane and it is not the
/// testing platform: it is Bobcat's own run bracket, which a projected suite opens on its first
/// scenario and closes at process exit. See <c>docs/warm-projected-runs.md</c>.
/// </para>
/// <para>
/// <b>It exits 75 after a restart</b>, exactly as a resident spec host does (issue #397), so one
/// parent can relaunch either on the same rule.
/// </para>
/// </remarks>
[Description("Keep a suite available to a monitor, running specifications when the monitor asks", Name = "resident")]
public class ResidentCommand : JasperFxAsyncCommand<ResidentInput>
{
    public ResidentCommand()
    {
        Usage("Register a suite with a monitor and run the specifications it asks for")
            .Arguments(x => x.HostPath);
    }

    public override async Task<bool> Execute(ResidentInput input)
    {
        OutOfProcessResidentSuite suite;

        try
        {
            suite = await OutOfProcessResidentSuite.For(
                input.HostPath,

                // The same origin the runner registers with, so a commanded run lands on the
                // console that asked for it rather than on whatever the child would resolve.
                monitorUrl: input.UrlFlag,
                log: System.Console.WriteLine);
        }
        catch (Exception e)
        {
            // Refused at launch rather than registered and then useless. Every one of these is a
            // wiring mistake with an answer in the message: a path that is not built, a suite with
            // no runner adapter, a framework whose filter spelling Bobcat will not guess at.
            System.Console.Error.WriteLine(e.Message);
            return false;
        }

        if (input.ListFlag)
        {
            System.Console.WriteLine(
                $"{suite.Suite} ({suite.Lane}/{suite.Manifest.Framework}), "
                + $"{suite.SpecIdentities.Count} specification(s):");

            foreach (var identity in suite.SpecIdentities) System.Console.WriteLine($"  {identity}");

            return true;
        }

        var options = new ResidentRunnerOptions
        {
            Url = input.UrlFlag,
            Log = System.Console.WriteLine
        };


        if (input.IdFlag is { Length: > 0 } id) options = options with { RunnerId = id };

        input.ExitCode = await ResidentMode.Run(suite, options);

        return true;
    }
}
