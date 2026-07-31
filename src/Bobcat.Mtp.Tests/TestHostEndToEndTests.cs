using System.Diagnostics;
using Shouldly;

namespace Bobcat.Mtp.Tests;

/// <summary>
/// Drives the sample Bobcat spec project as a real Microsoft.Testing.Platform executable —
/// the same way <c>dotnet test</c>, an IDE Test Explorer, or a future Bobcat supervisor would.
/// Unit tests can prove the mapping; only launching the process proves the host.
/// </summary>
public class TestHostEndToEndTests
{
    private static readonly string hostPath = locateSampleHost();

    private static string locateSampleHost()
    {
        // Walk up from the test assembly to the src root, then across to the sample host's
        // output for whatever configuration this build used.
        var configuration = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        var executable = Path.Combine(
            directory.FullName, "Bobcat.Mtp.SampleHost", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "Bobcat.Mtp.SampleHost.exe" : "Bobcat.Mtp.SampleHost");

        return executable;
    }

    private static async Task<(int ExitCode, string Output)> runHost(params string[] arguments)
    {
        File.Exists(hostPath).ShouldBeTrue($"The sample host was not built at {hostPath}");

        var info = new ProcessStartInfo(hostPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);

        return (process.ExitCode, stdout + stderr);
    }

    [Fact]
    public async Task the_platform_discovers_every_scenario_without_running_it()
    {
        var (exitCode, output) = await runHost("--list-tests");

        exitCode.ShouldBe(0);
        output.ShouldContain("Arithmetic: addition works");
        output.ShouldContain("Arithmetic: subtraction disagrees");
        output.ShouldContain("Inventory: restock is flaky");
        output.ShouldContain("found 5 test(s)");

        // Discovery must not execute anything — IDEs discover on every build, and a scenario
        // that ran would have started resources.
        output.ShouldNotContain("attempted to divide by zero");
    }

    [Fact]
    public async Task a_full_run_reports_each_scenario_with_the_right_outcome()
    {
        var (exitCode, output) = await runHost();

        output.ShouldContain("total: 5");
        output.ShouldContain("succeeded: 3");
        output.ShouldContain("failed: 2");

        // Non-zero because scenarios failed — this is what makes `dotnet test` and CI work.
        exitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task a_comparison_failure_reports_expected_and_actual()
    {
        var (_, output) = await runHost();

        output.ShouldContain("result: expected 4, got 5");
    }

    [Fact]
    public async Task an_escaped_exception_is_reported_with_its_type_and_message()
    {
        var (_, output) = await runHost();

        output.ShouldContain("System.InvalidOperationException");
        output.ShouldContain("attempted to divide by zero");
    }

    [Fact]
    public async Task a_single_scenario_can_be_run_alone_by_uid()
    {
        // The lever the #43 spike identified as load-bearing for the supervisor: selective
        // re-run and [Isolated] scheduling both depend on this working against a Bobcat host.
        var (exitCode, output) = await runHost("--filter-uid", "Arithmetic/addition works");

        exitCode.ShouldBe(0);
        output.ShouldContain("total: 1");
        output.ShouldContain("succeeded: 1");
    }

    [Fact]
    public async Task a_uid_filter_selecting_a_failing_scenario_runs_only_that_one()
    {
        var (exitCode, output) = await runHost("--filter-uid", "Arithmetic/subtraction disagrees");

        exitCode.ShouldNotBe(0);
        output.ShouldContain("total: 1");
        output.ShouldContain("failed: 1");
    }

    [Fact]
    public async Task scenario_uids_are_stable_across_separate_processes()
    {
        // A supervisor retries by uid in a later process. If identity drifted between runs the
        // retry would target the wrong test — or nothing at all.
        var first = await runHost("--list-tests");
        var second = await runHost("--list-tests");

        scenarioLines(first.Output).ShouldBe(scenarioLines(second.Output));
        scenarioLines(first.Output).ShouldNotBeEmpty();
    }

    private static List<string> scenarioLines(string output)
        => output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Arithmetic:") || line.StartsWith("Inventory:"))
            .Order()
            .ToList();
}
