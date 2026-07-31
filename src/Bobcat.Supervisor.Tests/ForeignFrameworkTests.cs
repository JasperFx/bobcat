using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The separability claim in issue #41, tested rather than asserted: the supervisor drives a
/// worker that has nothing to do with Bobcat's spec runner.
/// </summary>
/// <remarks>
/// The worker here is <c>Bobcat.Tests</c> — an ordinary xUnit v3 project. Nothing in the
/// supervisor knows that; it speaks MTP server mode and reads traits. The same code path would
/// drive a tUnit host.
/// </remarks>
public class ForeignFrameworkTests
{
    private static string xunitWorkerPath()
    {
        var configuration = Path.GetFileName(
            Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, "Bobcat.Tests", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "Bobcat.Tests.exe" : "Bobcat.Tests");
    }

    [Fact]
    public async Task the_supervisor_can_discover_and_run_a_single_xunit_test_by_uid()
    {
        var path = xunitWorkerPath();
        File.Exists(path).ShouldBeTrue($"The xUnit worker was not built at {path}");

        await using var worker = await MtpWorkerClient.Launch(path);

        var tests = await worker.Discover();
        tests.Count.ShouldBeGreaterThan(50);

        // xUnit's uids are opaque hashes, not names — so the realistic flow is discover, match on
        // display name, then run by uid. The supervisor never parses a uid.
        var target = tests.First(t => t.DisplayName.Contains("none_allows_a_single_attempt_and_no_retries"));
        target.Uid.ShouldNotBe(target.DisplayName);

        var result = await worker.Run([target.Uid]);

        // Exactly one test ran — the selective re-run lever, against a foreign framework.
        var outcome = result.Outcomes.ShouldHaveSingleItem();
        outcome.Uid.ShouldBe(target.Uid);
        outcome.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task xunit_uids_are_stable_across_separate_processes()
    {
        var path = xunitWorkerPath();

        var first = await discoverUids(path);
        var second = await discoverUids(path);

        // A retry happens in a later process. If identity drifted, it would target nothing.
        first.ShouldBe(second);
        first.ShouldNotBeEmpty();
    }

    private static async Task<List<string>> discoverUids(string path)
    {
        await using var worker = await MtpWorkerClient.Launch(path);
        var tests = await worker.Discover();
        return tests.Select(t => t.Uid).Order().ToList();
    }
}
