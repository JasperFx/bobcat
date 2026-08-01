using Bobcat.Monitoring;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The supervisor as the run's monitor-facing owner: one bracket for the whole run, and the
/// grouping environment (<c>BOBCAT_RUN_ID</c> + <c>BOBCAT_RUN_OWNER</c>) on every worker
/// launch, so a supervised suite is one dashboard card instead of one per worker process.
/// </summary>
public class SupervisorMonitorPublishingTests
{
    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = new();

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }

    private static FakeWorkerFactory threeTests() => new()
    {
        Tests =
        [
            FakeWorkerFactory.Test("clean"),
            FakeWorkerFactory.Test("flaky", "Retry=2"),
            FakeWorkerFactory.Test("broken")
        ],
        Outcome = (uid, attempt, _) => uid switch
        {
            "clean" => WorkerTestState.Passed,
            "flaky" => attempt == 1 ? WorkerTestState.Failed : WorkerTestState.Passed,
            _ => WorkerTestState.Failed
        }
    };

    [Fact]
    public async Task the_supervisor_owns_the_bracket_and_hands_every_worker_the_grouping_pair()
    {
        var sink = new RecordingSink();
        var factory = threeTests();
        var supervisor = new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 },
            PublishToMonitor = true,
            MonitorSink = sink
        };

        var results = await supervisor.Run();

        // The bracket: opened with the true post-filter total (which no single worker knows),
        // closed with the honest counts — pass-on-retry never folded into clean passes.
        var started = sink.Events.First().ShouldBeOfType<RunStarted>();
        started.Mode.ShouldBe("supervised");
        started.Suite.ShouldBe("fake");
        started.TotalScenarios.ShouldBe(3);

        var finished = sink.Events.Last().ShouldBeOfType<RunFinished>();
        finished.RunId.ShouldBe(started.RunId);
        finished.ExitCode.ShouldBe(results.ExitCode);
        finished.Passed.ShouldBe(1);
        finished.PassedOnRetry.ShouldBe(1);
        finished.Failed.ShouldBe(1);
        finished.Indeterminate.ShouldBe(0);

        // Every launch — discovery included — carried the grouping pair, so worker publishers
        // join this run as participants instead of announcing their own.
        factory.Launched.ShouldNotBeEmpty();
        foreach (var worker in factory.Launched)
        {
            var environment = worker.Launch.Environment.ShouldNotBeNull();
            environment[MonitorRunInfo.RunIdVariable].ShouldBe(started.RunId.ToString());
            environment[MonitorRunInfo.RunOwnerVariable].ShouldBe("supervisor");
        }
    }

    [Fact]
    public async Task a_crashed_worker_reports_indeterminate_not_failed()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("vanished")],
            // The worker reports nothing and dies — silence is absence of evidence.
            Outcome = (_, _, _) => null,
            Fault = _ => "worker exited with code 134"
        };

        var supervisor = new Supervisor(factory) { PublishToMonitor = true, MonitorSink = sink };
        var results = await supervisor.Run();
        results.ExitCode.ShouldBe(2);

        var finished = sink.Events.OfType<RunFinished>().Single();
        finished.Indeterminate.ShouldBe(1);
        // "We don't know what happened" is not folded into Failed — same split as exit 2 vs 1.
        finished.Failed.ShouldBe(0);
        finished.ExitCode.ShouldBe(2);
    }

    [Fact]
    public async Task publishing_is_opt_in_so_a_plain_run_neither_posts_nor_tags_workers()
    {
        var sink = new RecordingSink();
        var factory = threeTests();
        // MonitorSink set but PublishToMonitor left at its default of false — a unit test
        // driving a supervisor must never publish to a monitor that happens to be running.
        var supervisor = new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 },
            MonitorSink = sink
        };

        await supervisor.Run();

        sink.Events.ShouldBeEmpty();
        factory.Launched.ShouldAllBe(w => w.Launch.Environment == null);
    }

    [Fact]
    public void the_launch_context_environment_is_the_lowest_layer_of_the_stack()
    {
        var factory = new MtpWorkerFactory("worker.exe", new Dictionary<string, string>
        {
            ["SHARED"] = "factory",
            ["BOTH"] = "factory"
        })
        {
            EnvironmentFor = _ => new Dictionary<string, string> { ["BOTH"] = "lane", ["LANE"] = "lane" }
        };

        var context = new WorkerLaunchContext(0, WorkerPurpose.Lane)
        {
            Environment = new Dictionary<string, string>
            {
                ["BOBCAT_RUN_ID"] = "run",
                ["SHARED"] = "context",
                ["BOTH"] = "context"
            }
        };

        var merged = factory.environmentFor(context).ShouldNotBeNull();

        // The supervisor proposes, the factory disposes: context is the baseline, the
        // factory's shared environment overrides it, and the per-lane hook overrides both.
        merged["BOBCAT_RUN_ID"].ShouldBe("run");
        merged["SHARED"].ShouldBe("factory");
        merged["BOTH"].ShouldBe("lane");
        merged["LANE"].ShouldBe("lane");
    }
}
