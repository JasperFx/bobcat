using System.Diagnostics;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#545 on a real TUnit host: <c>Bobcat.TUnit</c> reports a failing Acceptance spec SKIPPED
/// through TUnit's own <c>OverrideResult</c>, and <c>BOBCAT_LIFECYCLE=strict</c> leaves it failed.
/// </summary>
public class TUnitAcceptanceLifecycleTests
{
    private const string filter = "/*/Bobcat.TUnit.Tests.Lifecycle/*/*";

    [TUnitHostedFact]
    public async Task a_failing_acceptance_spec_is_skipped_with_its_reason_and_the_run_passes()
    {
        var (exitCode, output) = await run(strict: false);

        exitCode.ShouldBe(0, output);
        output.ShouldContain("failed: 0");
        output.ShouldContain("succeeded: 1");
        output.ShouldContain("skipped: 1");
        output.ShouldContain(
            "acceptance spec, not yet passing: System.NotImplementedException: the behaviour this specification describes");
    }

    [TUnitHostedFact]
    public async Task strict_mode_leaves_it_failed()
    {
        var (exitCode, output) = await run(strict: true);

        exitCode.ShouldNotBe(0, output);
        output.ShouldContain("failed: 1");
        output.ShouldContain("skipped: 0");
    }

    private static async Task<(int ExitCode, string Output)> run(bool strict)
    {
        var host = WorkerHosts.Locate("Bobcat.TUnit.Tests");
        File.Exists(host).ShouldBeTrue($"The TUnit host was not built at {host}");

        var info = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("--treenode-filter");
        info.ArgumentList.Add(filter);
        info.ArgumentList.Add("--no-banner");
        info.ArgumentList.Add("--output");
        info.ArgumentList.Add("Detailed");
        info.Environment["BOBCAT_MONITOR"] = "0";
        info.Environment[SpecLifecycle.EnvironmentVariable] = strict ? SpecLifecycle.StrictValue : "";

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await output + await error);
    }
}
