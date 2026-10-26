using Bobcat.Console.Specs;
using Bobcat.Monitoring;
using Bobcat.Residency;
using Bobcat.Supervisor;
using JasperFx.CommandLine;

namespace Bobcat.Console;

public class ResidentInput
{
    [Description("The spec project's test host: its executable, its build output folder (bin/Debug/net10.0), or its .csproj")]
    public string HostPath { get; set; } = string.Empty;

    [Description("With a .csproj: the target framework whose build to run; the newest the project targets by default")]
    [FlagAlias("framework", true)]
    public string? FrameworkFlag { get; set; }

    [Description("With a .csproj: the build configuration whose build to run; Debug by default")]
    [FlagAlias("configuration", true)]
    public string ConfigurationFlag { get; set; } = "Debug";

    [Description("Base URL of the monitor to register with; several, comma-separated, publish a commanded run to each. Defaults to BOBCAT_MONITOR_URL, then to the port every publisher probes")]
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
/// <b>Why the tool and not the suite.</b> A suite's entry point belongs to its test framework
/// (xUnit, TUnit, NUnit, MSTest), not to Bobcat, so something outside it has to hold the manifest and launch a
/// filtered host per command — and that something has to ship from here, because the console at
/// the other end references nothing in this repository and so cannot be handed a class to host.
/// The <c>bobcat</c> tool is where a run-side executable with no server in it belongs.
/// </para>
/// <para>
/// <b>Cold and warm (issue #402).</b> A cold command launches a fresh filtered host, which is what
/// this command always did. A <b>warm</b> command runs in a host held open in the testing
/// platform's server mode — Microsoft's platform takes repeated run requests in one live process,
/// and the blocker was Bobcat's own run bracket, which is now per request rather than per process.
/// Warmth here keeps the CLR warm (JIT, loaded assemblies, codegen) and not the suite's own
/// fixtures, which its test framework creates and disposes inside each request; see
/// <c>docs/warm-projected-runs.md</c>. A console chooses per command, so nothing changes for
/// anyone who never asks for warm.
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
        // The executable, or the folder or .csproj that leads to it (bobcat#785). A path that leads
        // nowhere is a usage error: one line, exit 1, no stack trace.
        var host = await TestHostPath.ResolveAsync(input.HostPath, input.FrameworkFlag, input.ConfigurationFlag);

        WarmProjectedResidentSuite suite;

        try
        {
            // Warm-capable, which subsumes cold: a cold command is delegated to
            // OutOfProcessResidentSuite unchanged, so this is strictly additive.
            suite = await WarmProjectedResidentSuite.For(
                host,

                // The same origins the runner was pointed at, whole, so a commanded run lands on
                // the console that asked for it rather than on whatever the child would resolve —
                // and on every other console the list names (bobcat#530).
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
            // Commands come from ONE console, while the run they ask for may be published to
            // several (bobcat#530) — so the whole list went to the suite above, and the first
            // entry is where this runner registers and reads its command stream. Null leaves the
            // runner to resolve BOBCAT_MONITOR_URL's first target for itself.
            Url = input.UrlFlag is { Length: > 0 } flag ? MonitorPublisher.ParseUrls(flag)[0] : null,
            Log = System.Console.WriteLine
        };


        if (input.IdFlag is { Length: > 0 } id) options = options with { RunnerId = id };

        await using (suite)
        {
            input.ExitCode = await ResidentMode.Run(suite, options);
        }

        return true;
    }
}
