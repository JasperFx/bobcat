using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// The parts that can be established without a Docker daemon: how <c>docker compose ps</c> output
/// is read, and how the command line is built. The daemon-dependent behaviour is covered by
/// <see cref="DockerComposeIntegrationTests"/>.
/// </summary>
public class DockerComposeResourceTests
{
    // Real output shapes, captured from docker compose 5.3.1.
    private const string NdJson =
        """
        {"Service":"postgres","State":"running","Health":"healthy","ExitCode":0}
        {"Service":"rabbitmq","State":"running","Health":"","ExitCode":0}
        """;

    [Fact]
    public void ndjson_output_is_read_one_object_per_line()
    {
        // Current compose emits newline-delimited objects rather than an array.
        var statuses = DockerComposeResource.ParseStatus(NdJson);

        statuses.Count.ShouldBe(2);
        statuses[0].Service.ShouldBe("postgres");
        statuses[0].Health.ShouldBe("healthy");
    }

    [Fact]
    public void the_older_array_shape_is_read_too()
    {
        var statuses = DockerComposeResource.ParseStatus(
            """[{"Service":"sqlserver","State":"running","Health":"starting"}]""");

        statuses.ShouldHaveSingleItem().Health.ShouldBe("starting");
    }

    [Fact]
    public void an_empty_health_means_no_healthcheck_declared_not_unhealthy()
    {
        // The distinction the whole tiered design rests on. Reading "" as unhealthy would fail
        // every container that simply never declared a healthcheck.
        var statuses = DockerComposeResource.ParseStatus(NdJson);

        statuses[1].Health.ShouldBe("");
        statuses[1].State.ShouldBe("running");
    }

    [Fact]
    public void no_containers_parses_to_nothing_rather_than_throwing()
    {
        DockerComposeResource.ParseStatus("").ShouldBeEmpty();
        DockerComposeResource.ParseStatus("   \n  ").ShouldBeEmpty();
    }

    [Fact]
    public void compose_file_flags_belong_to_the_compose_subcommand()
    {
        // `docker -f x compose up` is not a valid command line; `docker compose -f x up` is.
        var resource = new DockerComposeResource("db")
            .UsingComposeFile("docker-compose.yml")
            .UsingComposeFile("docker-compose.override.yml");

        resource.ArgumentsFor(["up", "-d"])
            .ShouldBe(["compose", "-f", "docker-compose.yml", "-f", "docker-compose.override.yml", "up", "-d"]);
    }

    [Fact]
    public void with_no_compose_file_docker_is_left_to_discover_one()
    {
        new DockerComposeResource("db").ArgumentsFor(["ps"]).ShouldBe(["compose", "ps"]);
    }

    [Fact]
    public void a_resource_is_recyclable_so_a_policy_can_ask_for_it_by_name()
    {
        // @recycle(rabbit) resolves against Name, so the supervisor can find it.
        var resource = new DockerComposeResource("rabbit");

        resource.ShouldBeAssignableTo<IRecyclableResource>();
        resource.Name.ShouldBe("rabbit");
    }

    [Fact]
    public async Task resetting_between_scenarios_does_nothing()
    {
        // Containers are recycled or left alone — never reset per scenario, which would be
        // ruinously slow and is not what reset means.
        await new DockerComposeResource("db").ResetBetweenScenarios();
    }

    [Fact]
    public async Task disposing_leaves_containers_up_by_default()
    {
        // If this ran `docker compose down` it would fail here (no daemon call is configured),
        // and locally it would throw away the container that makes the next run fast.
        await new DockerComposeResource("db").DisposeAsync();
    }
}
