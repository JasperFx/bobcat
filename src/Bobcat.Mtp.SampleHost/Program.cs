using Bobcat.CodeFirst;
using Bobcat.Mtp;
using Bobcat.Runtime;

namespace Bobcat.Mtp.SampleHost;

/// <summary>
/// A Bobcat spec project running as an MTP test host. Features are written code-first rather
/// than generated so the outcomes are exact and the end-to-end tests can assert on them.
/// </summary>
public static class Program
{
    public static Task<int> Main(string[] args)
        => BobcatTestApplication.Run(args, runner =>
        {
            runner.AddSpecification<Arithmetic>();
            runner.AddSpecification<Inventory>();

            // Two resources, registered in this order, so the end-to-end tests can prove that
            // when the second one fails to start the first one is still torn down. Both are
            // inert unless armed through the environment.
            runner.Suite.AddResource(new LifecycleLoggingResource("database"));
            runner.Suite.AddResource(new BrokerThatWillNotStart());
        });

    /// <summary>
    /// Appends each lifecycle call to the file named by <c>BOBCAT_LIFECYCLE_LOG</c>, so a test in
    /// another process can read back what the host did. Silent when not armed.
    /// </summary>
    private sealed class LifecycleLoggingResource(string name) : ITestResource
    {
        public string Name { get; } = name;

        public Task Start() => log("start");
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public async ValueTask DisposeAsync() => await log("dispose");

        private Task log(string what)
        {
            var path = Environment.GetEnvironmentVariable("BOBCAT_LIFECYCLE_LOG");
            if (path is not null) File.AppendAllText(path, $"{Name}:{what}{Environment.NewLine}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws from <see cref="Start"/> when <c>BOBCAT_START_FAILS</c> is set — the broker that is
    /// down this morning. Issue #123: this used to take the whole host process down with it.
    /// </summary>
    private sealed class BrokerThatWillNotStart : ITestResource
    {
        public string Name => "broker";

        public Task Start()
        {
            if (Environment.GetEnvironmentVariable("BOBCAT_START_FAILS") == "true")
            {
                throw new InvalidOperationException("the broker refused the connection");
            }

            return Task.CompletedTask;
        }

        public Task ResetBetweenScenarios() => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            var path = Environment.GetEnvironmentVariable("BOBCAT_LIFECYCLE_LOG");
            if (path is not null) File.AppendAllText(path, $"{Name}:dispose{Environment.NewLine}");
            return ValueTask.CompletedTask;
        }
    }

    public class Arithmetic : Specification
    {
        [Scenario]
        public void addition_works() => Then("2 + 2", () => 2 + 2).ShouldBe(4);

        /// <summary>A comparison that disagrees: MTP state <c>failed</c>, with expected and actual.</summary>
        [Scenario]
        public void subtraction_disagrees() => Then("9 - 4", () => 9 - 4).ShouldBe(4);

        /// <summary>An exception that escapes: MTP state <c>error</c>, with the type and message.</summary>
        [Scenario]
        public void division_explodes()
            => When("dividing by zero", () => throw new InvalidOperationException("attempted to divide by zero"));
    }

    public class Inventory : Specification
    {
        [Scenario(Tags = ["regression"])]
        public void stock_is_counted() => Then("the stock is counted", () => { });

        [Scenario(Tags = ["isolated", "recycle(rabbit)"])]
        public void restock_is_flaky() => Then("the restock completes", () => { });
    }
}
