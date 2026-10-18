using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>bobcat#702 — the shipped extra sampling targets.</summary>
public class MemorySampleTargetTests
{
    private const long MiB = 1024 * 1024;

    [Theory]
    [InlineData("312.5MiB / 7.656GiB", (long)(312.5 * MiB))]
    [InlineData("1.25GiB / 15.6GiB", (long)(1.25 * 1024 * MiB))]
    [InlineData("812KiB / 1GiB", 812 * 1024L)]
    [InlineData("0B / 0B", 0L)]
    [InlineData("150MB / 2GB", 150_000_000L)]
    [InlineData("  45.1kB / 1GB\n", 45_100L)]
    public void docker_stats_mem_usage_is_read_as_bytes(string output, long expected)
        => DockerContainerMemory.ParseMemUsage(output).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("--")]
    [InlineData("Error: No such container: postgres")]
    [InlineData("12 parsecs / 1GiB")]
    public void anything_else_is_unmeasured_never_zero(string output)
        => DockerContainerMemory.ParseMemUsage(output).ShouldBeNull();

    [Fact]
    public void a_container_target_asks_docker_stats_for_one_snapshot_of_that_container()
    {
        var target = (DockerContainerMemory)MemorySampleTargets.Container("bobcat-postgres-1", "postgres");

        target.Name.ShouldBe("postgres");
        target.Arguments.ShouldBe(["stats", "--no-stream", "--format", "{{.MemUsage}}", "bobcat-postgres-1"]);
        MemorySampleTargets.Container("pg").Name.ShouldBe("pg");
    }

    [Fact]
    public async Task a_process_target_reads_the_pid_it_is_handed_each_time()
    {
        var target = MemorySampleTargets.Process("self", () => Environment.ProcessId);
        (await target.SampleBytes(CancellationToken.None)).ShouldNotBeNull().ShouldBeGreaterThan(0);

        var none = MemorySampleTargets.Process("gone", () => null);
        (await none.SampleBytes(CancellationToken.None)).ShouldBeNull();
    }
}
