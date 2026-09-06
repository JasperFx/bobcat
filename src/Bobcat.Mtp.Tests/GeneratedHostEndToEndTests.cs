using System.Diagnostics;
using Shouldly;

namespace Bobcat.Mtp.Tests;

/// <summary>
/// Drives <c>Bobcat.Mtp.GeneratedHost</c> — the spec project with no hand-written <c>Main</c>
/// anywhere — as a real Microsoft.Testing.Platform executable. This is the end-to-end proof of
/// issue #207's generated entry point: the generator emitted the <c>Main</c>, the
/// <c>[BobcatConfiguration]</c> seam ran, and the friendly filters work through the same host.
/// </summary>
public class GeneratedHostEndToEndTests
{
    private static readonly string hostPath = locateHost();

    private static string locateHost()
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, "Bobcat.Mtp.GeneratedHost", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "Bobcat.Mtp.GeneratedHost.exe" : "Bobcat.Mtp.GeneratedHost");
    }

    private static async Task<(int ExitCode, string Output)> runHost(
        IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        File.Exists(hostPath).ShouldBeTrue($"The generated host was not built at {hostPath}");

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
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) info.Environment[key] = value;

        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token);

        return (process.ExitCode, stdout + stderr);
    }

    private static Task<(int ExitCode, string Output)> runHost(params string[] arguments)
        => runHost(null, arguments);

    // --- The generated entry point itself.

    [Fact]
    public async Task the_generated_entry_point_discovers_every_scenario()
    {
        var (exitCode, output) = await runHost("--list-tests");

        exitCode.ShouldBe(0);
        output.ShouldContain("Ordering: An order is accepted");
        output.ShouldContain("Ordering: An order can be emptied");
        output.ShouldContain("Shipping: A shipment is labelled");
        output.ShouldContain("found 3 test(s)");
    }

    [Fact]
    public async Task a_full_run_passes_through_the_generated_entry_point()
    {
        var (exitCode, output) = await runHost();

        exitCode.ShouldBe(0);
        output.ShouldContain("total: 3");
        output.ShouldContain("succeeded: 3");
    }

    [Fact]
    public async Task the_configuration_seam_actually_ran()
    {
        // The [BobcatConfiguration] method registers a resource that logs its lifecycle when
        // armed. If the generated Main skipped the seam, the suite would still pass — this is
        // the test that proves the resource was really registered and started.
        var log = Path.Combine(Path.GetTempPath(), $"bobcat-generated-host-{Guid.NewGuid():N}.log");
        try
        {
            var (exitCode, _) = await runHost(
                new Dictionary<string, string> { ["BOBCAT_GENERATED_HOST_LOG"] = log });

            exitCode.ShouldBe(0);
            var events = File.ReadAllLines(log);
            events.ShouldContain("probe:start");
            events.ShouldContain("probe:dispose");
        }
        finally
        {
            File.Delete(log);
        }
    }

    [Fact]
    public async Task discovery_does_not_start_resources()
    {
        var log = Path.Combine(Path.GetTempPath(), $"bobcat-generated-host-{Guid.NewGuid():N}.log");
        try
        {
            await runHost(
                new Dictionary<string, string> { ["BOBCAT_GENERATED_HOST_LOG"] = log },
                "--list-tests");

            File.Exists(log).ShouldBeFalse("discovery must never start resources — IDEs discover on every build");
        }
        finally
        {
            if (File.Exists(log)) File.Delete(log);
        }
    }

    // --- The friendly filters (issue #207 item 2).

    [Fact]
    public async Task a_feature_filter_runs_only_that_features_scenarios()
    {
        var (exitCode, output) = await runHost("--filter-feature", "Ordering");

        exitCode.ShouldBe(0);
        output.ShouldContain("total: 2");
        output.ShouldContain("succeeded: 2");
    }

    [Fact]
    public async Task a_feature_filter_is_a_case_insensitive_substring()
    {
        var (exitCode, output) = await runHost("--filter-feature", "shipp");

        exitCode.ShouldBe(0);
        output.ShouldContain("total: 1");
    }

    [Fact]
    public async Task a_tag_filter_runs_only_tagged_scenarios()
    {
        var (exitCode, output) = await runHost("--filter-tag", "regression");

        exitCode.ShouldBe(0);
        output.ShouldContain("total: 1");
        output.ShouldContain("succeeded: 1");
    }

    // Note: `--filter-tag @regression` cannot work from the command line — the platform
    // consumes any @-prefixed argument as a response-file reference before option parsing
    // ever sees it. The tag is written bare; SpecFilters still trims a leading @ for
    // programmatic callers, pinned in SpecFiltersTests.

    [Fact]
    public async Task feature_and_tag_filters_intersect()
    {
        // Shipping has no @regression scenario, so the intersection is empty — and MTP treats
        // zero tests as its own failure (exit 8), which is the honest outcome for a filter
        // matching nothing.
        var (exitCode, output) = await runHost("--filter-feature", "Shipping", "--filter-tag", "regression");

        exitCode.ShouldNotBe(0);
        output.ShouldContain("Zero tests ran");
    }

    [Fact]
    public async Task the_friendly_filters_narrow_discovery_too()
    {
        var (exitCode, output) = await runHost("--list-tests", "--filter-feature", "Shipping");

        exitCode.ShouldBe(0);
        output.ShouldContain("Shipping: A shipment is labelled");
        output.ShouldNotContain("Ordering:");
        output.ShouldContain("found 1 test(s)");
    }

    [Fact]
    public async Task the_uid_filter_still_works_alongside_the_friendly_ones()
    {
        var (exitCode, output) = await runHost("--filter-uid", "Ordering/An order is accepted");

        exitCode.ShouldBe(0);
        output.ShouldContain("total: 1");
    }
}
