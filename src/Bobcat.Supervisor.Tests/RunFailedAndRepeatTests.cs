using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#572: <c>bobcat run</c> always leaves <c>obj/bobcat/last-run.json</c> behind, and
/// <c>--failed</c> / <c>--repeat</c> run from it. Driven through the <c>bobcat</c> executable
/// against <c>Bobcat.Supervisor.ProjectedWorker</c>, whose <c>Basics/always fails</c> is red on
/// purpose — the file is the tool's, so a class nothing ships would prove nothing.
/// </summary>
/// <remarks>
/// One class, so its tests run one at a time: they share the worker project's state file.
/// </remarks>
public class RunFailedAndRepeatTests : IDisposable
{
    /// <summary>The <c>bobcat</c> tool, whose assembly is <c>Bobcat.Cli</c> (see Bobcat.Console.csproj).</summary>
    private static readonly string tool = Path.Combine(
        Path.GetDirectoryName(WorkerHosts.Locate("Bobcat.Console"))!,
        OperatingSystem.IsWindows() ? "Bobcat.Cli.exe" : "Bobcat.Cli");

    private static readonly string configuration = Path.GetFileName(
        Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

    private static readonly string project = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(WorkerHosts.ProjectedWorker)!, "..", "..", "..", "Bobcat.Supervisor.ProjectedWorker.csproj"));

    private static readonly string lastRun = Path.Combine(Path.GetDirectoryName(project)!, "obj", "bobcat", "last-run.json");

    public RunFailedAndRepeatTests() => forget();

    public void Dispose() => forget();

    private static void forget()
    {
        if (File.Exists(lastRun)) File.Delete(lastRun);
    }

    private static async Task<(int ExitCode, string Output, string Error)> bobcat(params string[] arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in (string[])["run", project, "--no-build", "-c", configuration, .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["BOBCAT_MONITOR"] = "0";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await output, await error);
    }

    private static JsonNode recorded() => JsonNode.Parse(File.ReadAllText(lastRun))!;

    private static string[] strings(JsonNode? array) => array!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    private static string[] scenariosIn(string resultsJson)
    {
        using var document = JsonDocument.Parse(resultsJson);
        return document.RootElement.GetProperty("scenarios").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()!)
            .ToArray();
    }

    [Fact]
    public async Task a_run_records_what_failed_and_failed_reruns_exactly_that()
    {
        var first = await bobcat("--feature", "Basics");
        first.ExitCode.ShouldNotBe(0, first.Output);

        var state = recorded();
        strings(state["failed"]).ShouldBe(["Basics/always fails"]);
        state["resultsRead"]!.GetValue<bool>().ShouldBeTrue();
        state["exitCode"]!.GetValue<int>().ShouldBe(2);
        strings(state["arguments"]!["selection"]).ShouldContain("Basics/passes");
        strings(state["arguments"]!["selection"]).ShouldNotContain(x => !x.StartsWith("Basics/"));

        // Reserved for seeded order (C8): present now so --repeat needs no format change later
        state.AsObject().ContainsKey("seed").ShouldBeTrue();

        var rerun = await bobcat("--failed", "--json");
        rerun.ExitCode.ShouldNotBe(0, rerun.Error);
        scenariosIn(rerun.Output).ShouldBe(["Basics/always fails"]);

        // The rerun is itself the last run now, narrowed to what it ran
        strings(recorded()["arguments"]!["selection"]).ShouldBe(["Basics/always fails"]);
    }

    [Fact]
    public async Task repeat_replays_the_selection_and_flags()
    {
        var first = await bobcat("--feature", "Basics", "--scenario", "also passes", "--json");
        first.ExitCode.ShouldBe(0, first.Error);
        strings(recorded()["failed"]).ShouldBeEmpty();

        // --json is replayed too: standard output is the results file again
        var again = await bobcat("--repeat");
        again.ExitCode.ShouldBe(0, again.Error);
        scenariosIn(again.Output).ShouldBe(["Basics/also passes"]);
    }

    [Fact]
    public async Task an_identity_the_suite_no_longer_has_is_refused_by_name()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lastRun)!);
        File.WriteAllText(lastRun, """
            {
              "version": 1,
              "timestamp": "2026-10-16T00:00:00+00:00",
              "exitCode": 2,
              "resultsRead": true,
              "failed": [ "Basics/always fails", "Basics/renamed since" ],
              "arguments": { "framework": "net10.0", "configuration": "Debug", "selection": [] },
              "seed": null
            }
            """);

        var refused = await bobcat("--failed");

        refused.ExitCode.ShouldNotBe(0);
        refused.Output.ShouldContain("Basics/renamed since");
        refused.Output.ShouldNotContain("Specifications failed");

        // Nothing ran, so nothing was recorded over it
        strings(recorded()["failed"]).ShouldContain("Basics/renamed since");
    }

    [Fact]
    public async Task nothing_failed_says_so_and_exits_zero()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lastRun)!);
        File.WriteAllText(lastRun, """
            { "version": 1, "timestamp": "2026-10-16T00:00:00+00:00", "exitCode": 0, "resultsRead": true,
              "failed": [], "arguments": { "selection": [] }, "seed": null }
            """);

        var result = await bobcat("--failed");

        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("Nothing failed");
    }

    [Fact]
    public async Task failed_with_no_last_run_is_refused()
    {
        var result = await bobcat("--failed");

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("no last run");
    }
}
