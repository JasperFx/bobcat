using System.Diagnostics;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#539 on a real TUnit host: after a <c>SpecCatastrophicException</c>, every scenario that
/// opens later in the process fails without running, including after a <c>[Before(Class)]</c> hook
/// threw it. The probes are <c>Bobcat.TUnit.Tests/Halt</c>; one case per child process, because the
/// halt is process-wide.
/// </summary>
public class TUnitHaltTests
{
    [TUnitHostedFact]
    public async Task a_class_hook_that_throws_catastrophically_halts_every_later_scenario()
    {
        var run = await execute("fixture", "(FixtureLoss)|(AfterFixtureLoss)");

        run.Console.ShouldContain("the app did not start");
        run.Published["Halt after a fixture/a later scenario"]
            .ShouldBe("Environment lost: the app did not start (this scenario did not run)", run.Console);
    }

    [TUnitHostedFact]
    public async Task a_scenario_that_throws_catastrophically_halts_every_later_scenario_and_names_itself()
    {
        var run = await execute("scenario", "(ScenarioLoss)|(AfterScenarioLoss)");

        run.Published["Halt after a scenario/loses the database"]
            .ShouldBe("Bobcat.Engine.SpecCatastrophicException: the database is gone", run.Console);
        run.Published["Halt after a scenario/a later scenario"].ShouldBe(
            "Environment lost in Halt after a scenario/loses the database: the database is gone (this scenario did not run)",
            run.Console);
        run.ExitCode.ShouldNotBe(0, run.Console);
    }

    [TUnitHostedFact]
    public async Task without_a_catastrophic_failure_the_probes_pass()
    {
        var run = await execute("none", "(ScenarioLoss)|(AfterScenarioLoss)");

        run.Published["Halt after a scenario/loses the database"].ShouldBe("CleanPass", run.Console);
        run.Published["Halt after a scenario/a later scenario"].ShouldBe("CleanPass", run.Console);
        run.ExitCode.ShouldBe(0, run.Console);
    }

    private sealed record Run(Dictionary<string, string> Published, string Console, int ExitCode);

    private static async Task<Run> execute(string probeCase, string classes)
    {
        var host = WorkerHosts.Locate("Bobcat.TUnit.Tests");
        File.Exists(host).ShouldBeTrue($"The TUnit host was not built at {host}");

        var output = Path.Combine(Path.GetTempPath(), $"bobcat-tunit-halt-{Guid.NewGuid():N}.txt");
        try
        {
            var info = new ProcessStartInfo(host)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            info.ArgumentList.Add("--treenode-filter");
            info.ArgumentList.Add($"/*/Bobcat.TUnit.Tests.Halt/{classes}/*");
            info.ArgumentList.Add("--no-banner");
            info.ArgumentList.Add("--no-ansi");
            info.Environment["BOBCAT_HALT_PROBE"] = probeCase;
            info.Environment["BOBCAT_HALT_PROBE_OUT"] = output;
            info.Environment["BOBCAT_MONITOR"] = "0";

            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            var published = File.Exists(output)
                ? File.ReadAllLines(output).Select(x => x.Split('\t', 2)).ToDictionary(x => x[0], x => x[1])
                : [];

            return new Run(published, $"exit {process.ExitCode}\nstdout:\n{await stdout}\nstderr:\n{await stderr}",
                process.ExitCode);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
