using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Against the real daemon and this repository's own <c>docker-compose.yml</c>, which declares a
/// healthcheck on its Postgres. Skipped when Docker is not reachable, except on CI.
/// </summary>
public class DockerComposeIntegrationTests
{
    private static DockerComposeResource resource(Action<string>? log = null) =>
        new DockerComposeResource("postgres")
        {
            Services = ["postgres"],
            WorkingDirectory = repositoryRoot(),
            StartTimeout = TimeSpan.FromMinutes(2),
            Log = log
        };

    [DockerFact]
    public async Task readiness_comes_from_the_declared_healthcheck()
    {
        // The claim the whole design rests on: the compose file already knows how to test this
        // container, so Bobcat needs no Postgres-specific package to find out.
        var postgres = resource();

        await postgres.Start();

        postgres.ReadinessSource.ShouldBe("docker healthcheck");
    }

    [DockerFact]
    public async Task check_passes_against_a_healthy_container()
    {
        var postgres = resource();
        await postgres.Start();

        await postgres.Check(TestContext.Current.CancellationToken);
    }

    [DockerFact]
    public async Task a_service_that_does_not_exist_reports_something_a_human_can_act_on()
    {
        var missing = new DockerComposeResource("nope")
        {
            Services = ["no-such-service"],
            WorkingDirectory = repositoryRoot()
        };

        var thrown = await Should.ThrowAsync<Exception>(() => missing.Start());

        // Not "the process exited with 1" — it has to name the resource and suggest a cause.
        thrown.Message.ShouldContain("nope");
    }

    [DockerFact]
    public async Task a_recycle_replaces_the_container_and_waits_for_it_again()
    {
        // The expensive one, and the reason IRecyclableResource exists: a broker whose in-flight
        // state cannot be drained has to be thrown away, not restarted.
        var postgres = resource();
        await postgres.Start();

        var before = await containerIdOf("postgres");
        await postgres.Recycle(TestContext.Current.CancellationToken);
        var after = await containerIdOf("postgres");

        after.ShouldNotBe(before, "a recycle must replace the container, not restart it");
        postgres.ReadinessSource.ShouldBe("docker healthcheck");
    }

    private static async Task<string> containerIdOf(string service)
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker")
        {
            ArgumentList = { "compose", "ps", "-q", service },
            RedirectStandardOutput = true,
            WorkingDirectory = repositoryRoot()
        })!;

        var id = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return id.Trim();
    }

    private static string repositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository's docker-compose.yml.");
    }
}

/// <summary>
/// A fact that needs a reachable Docker daemon. Skips locally when there is none; never skips on
/// CI, so a missing daemon fails the build rather than reporting a silent pass — the same rule
/// <c>PostgresFact</c> follows.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> DaemonIsUp = new(() =>
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker")
            {
                ArgumentList = { "info" },
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null) return false;
            return process.WaitForExit(15_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    private static bool isCi =>
        Environment.GetEnvironmentVariable("CI")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    public DockerFactAttribute()
    {
        // Set in the constructor, matching PostgresFact: xUnit v3's Skip is not virtual.
        if (!isCi && !DaemonIsUp.Value)
        {
            Skip = "Docker is not reachable — run 'docker compose up -d'.";
        }
    }
}
