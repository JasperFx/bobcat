using Bobcat.Mtp;
using Bobcat.Runtime;

namespace Bobcat.Mtp.SampleHost;

/// <summary>
/// A Bobcat spec project running as an MTP test host. The features are written so the outcomes
/// are exact — one pass, one comparison failure, one error — and the end-to-end tests can assert
/// on them.
/// </summary>
public static class Program
{
    public static Task<int> Main(string[] args)
        => BobcatTestApplication.Run(args, runner =>
        {
            runner.ScanForFeatures(typeof(Program).Assembly);

            // Two resources, registered in this order, so the end-to-end tests can prove that
            // when the second one fails to start the first one is still torn down. Both are
            // inert unless armed through the environment.
            runner.Resources.Add(new LifecycleLoggingResource("database"));
            runner.Resources.Add(new BrokerThatWillNotStart());
        });

    /// <summary>
    /// Appends each lifecycle call to the file named by <c>BOBCAT_LIFECYCLE_LOG</c>, so a test in
    /// another process can read back what the host did. Silent when not armed.
    /// </summary>
    private sealed class LifecycleLoggingResource(string name) : ITestResource
    {
        public string Name { get; } = name;

        public Task StartAsync(CancellationToken cancellationToken = default) => log("start");
        public Task StopAsync(CancellationToken cancellationToken = default) => log("stop");
        public Task ResetBetweenScenarios() => Task.CompletedTask;

        private Task log(string what)
        {
            var path = Environment.GetEnvironmentVariable("BOBCAT_LIFECYCLE_LOG");
            if (path is not null) File.AppendAllText(path, $"{Name}:{what}{Environment.NewLine}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws from <see cref="StartAsync"/> when <c>BOBCAT_START_FAILS</c> is set — the broker that is
    /// down this morning. Issue #123: this used to take the whole host process down with it.
    /// </summary>
    private sealed class BrokerThatWillNotStart : ITestResource
    {
        public string Name => "broker";

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (Environment.GetEnvironmentVariable("BOBCAT_START_FAILS") == "true")
            {
                throw new InvalidOperationException("the broker refused the connection");
            }

            return Task.CompletedTask;
        }

        public Task ResetBetweenScenarios() => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            var path = Environment.GetEnvironmentVariable("BOBCAT_LIFECYCLE_LOG");
            if (path is not null) File.AppendAllText(path, $"{Name}:stop{Environment.NewLine}");
            return Task.CompletedTask;
        }
    }
}
