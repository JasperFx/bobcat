using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #414: each worker is handed complete connection strings as <c>ConnectionStrings__{name}</c>,
/// the Aspire convention, so a suite reads them through <c>IConfiguration.GetConnectionString</c>
/// however it was launched.
/// </summary>
public class WorkerConnectionStringsTests
{
    private static readonly WorkerLaunchContext lane2 = new(2, WorkerPurpose.Lane);

    [Fact]
    public void each_resource_is_a_connection_strings_variable_holding_the_whole_string()
    {
        var strings = new WorkerConnectionStrings()
            .Add("postgres", worker => $"Host=localhost;Database=specs_w{worker.Lane}")
            .Add("rabbitmq", "amqp://guest:guest@localhost:5672");

        strings.EnvironmentFor(lane2).ShouldBe(new Dictionary<string, string>
        {
            ["ConnectionStrings__postgres"] = "Host=localhost;Database=specs_w2",
            ["ConnectionStrings__rabbitmq"] = "amqp://guest:guest@localhost:5672"
        });
        strings.Names.ShouldBe(["postgres", "rabbitmq"]);
    }

    [Fact]
    public void the_provider_sees_which_worker_it_is_for()
    {
        // A throwaway database for discovery, say, without inferring it from the lane number
        var strings = new WorkerConnectionStrings()
            .Add("postgres", worker => worker.Purpose == WorkerPurpose.Discovery ? "Database=discovery" : $"Database=w{worker.Lane}");

        strings.EnvironmentFor(WorkerLaunchContext.Discovery)["ConnectionStrings__postgres"].ShouldBe("Database=discovery");
        strings.EnvironmentFor(new WorkerLaunchContext(1, WorkerPurpose.Lane))["ConnectionStrings__postgres"].ShouldBe("Database=w1");
    }

    [Fact]
    public void a_worker_with_no_connection_string_for_a_resource_is_refused_by_name()
    {
        var strings = new WorkerConnectionStrings().Add("postgres", _ => "");

        Should.Throw<InvalidOperationException>(() => strings.EnvironmentFor(lane2))
            .Message.ShouldStartWith("The connection string for 'postgres' was empty for the Lane worker on lane 2.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("Section:Key")]
    [InlineData("a=b")]
    public void a_name_neither_an_environment_variable_nor_configuration_can_carry_is_refused(string name)
    {
        Should.Throw<ArgumentException>(() => new WorkerConnectionStrings().Add(name, "Host=x"));
    }

    [Fact]
    public void names_that_differ_only_in_case_are_one_setting_so_the_second_is_refused()
    {
        var strings = new WorkerConnectionStrings().Add("postgres", "Host=a");

        Should.Throw<ArgumentException>(() => strings.Add("Postgres", "Host=b"))
            .Message.ShouldContain("already added");
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("my-service-bus")]
    [InlineData("kafka_1")]
    [InlineData("db.primary")]
    public void aspire_style_names_are_accepted(string name)
    {
        new WorkerConnectionStrings().Add(name, "x=y").Names.ShouldBe([name]);
    }

    // ── layering in MtpWorkerFactory ────────────────────────────────────────

    [Fact]
    public void connection_strings_layer_over_the_shared_environment_and_under_the_per_worker_one()
    {
        var factory = new MtpWorkerFactory("worker", new Dictionary<string, string>
        {
            ["SHARED"] = "shared",
            ["ConnectionStrings__postgres"] = "from the shared environment"
        })
        {
            ConnectionStrings = new WorkerConnectionStrings()
                .Add("postgres", worker => $"Database=w{worker.Lane}")
                .Add("rabbitmq", "amqp://localhost"),
            EnvironmentFor = _ => new Dictionary<string, string>
            {
                // The explicit per-worker hook is the most specific layer, so it still wins
                ["ConnectionStrings__rabbitmq"] = "amqp://override"
            }
        };

        var environment = environmentOf(factory, lane2)!;

        environment["SHARED"].ShouldBe("shared");
        environment["ConnectionStrings__postgres"].ShouldBe("Database=w2");
        environment["ConnectionStrings__rabbitmq"].ShouldBe("amqp://override");
    }

    [Fact]
    public void connection_strings_alone_need_no_other_environment()
    {
        var factory = new MtpWorkerFactory("worker")
        {
            ConnectionStrings = new WorkerConnectionStrings().Add("postgres", worker => $"Database=w{worker.Lane}")
        };

        environmentOf(factory, lane2).ShouldBe(new Dictionary<string, string>
        {
            ["ConnectionStrings__postgres"] = "Database=w2"
        });
    }

    private static IReadOnlyDictionary<string, string>? environmentOf(MtpWorkerFactory factory, WorkerLaunchContext context)
        => (IReadOnlyDictionary<string, string>?)typeof(MtpWorkerFactory)
            .GetMethod("environmentFor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(factory, [context]);
}
