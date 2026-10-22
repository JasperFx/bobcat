using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#768: a supervisor publishes under a tag of its caller's choosing, so several
/// supervisors in one process — a gate running every test project of a repository — each speak
/// for their own unit instead of all claiming the one <c>BOBCAT_RUN_TAG</c> the process was
/// launched with.
/// </summary>
/// <remarks>
/// In the <c>supervisor-monitor-env</c> collection and restoring the variable, for the reason
/// <c>SupervisorMonitorPublishingTests</c> gives: these tests set process-wide state, and a run
/// launched by a Stoat agent genuinely has <c>BOBCAT_RUN_TAG</c> set.
/// </remarks>
[Collection("supervisor-monitor-env")]
public class RunTagTests : IDisposable
{
    private readonly string? _previousTag
        = Environment.GetEnvironmentVariable(MonitorRunInfo.RunTagVariable);

    public void Dispose()
        => Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, _previousTag);

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

    private static FakeWorkerFactory onePassingTest() => new()
    {
        Tests = [FakeWorkerFactory.Test("clean")],
        Outcome = (_, _, _) => WorkerTestState.Passed
    };

    [Fact]
    public async Task run_tag_wins_over_the_process_environment_on_the_bracket_and_on_every_worker()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, "local-gates/bobcat-gate");

        var sink = new RecordingSink();
        var factory = onePassingTest();
        var supervisor = new Supervisor(factory)
        {
            PublishToMonitor = true,
            MonitorSink = sink,
            RunTag = "local-gates/bobcat-gate/Bobcat.Supervisor.Tests.net10.0"
        };

        await supervisor.Run();

        // The bracket speaks for the unit, not for whatever launched the process.
        sink.Events.First().ShouldBeOfType<RunStarted>().Tag
            .ShouldBe("local-gates/bobcat-gate/Bobcat.Supervisor.Tests.net10.0");

        // So does every worker — discovery included — beside the grouping pair it already carried,
        // so a worker's own records name the same unit its supervisor does.
        factory.Launched.ShouldNotBeEmpty();
        foreach (var worker in factory.Launched)
        {
            var environment = worker.Launch.Environment.ShouldNotBeNull();
            environment[MonitorRunInfo.RunTagVariable]
                .ShouldBe("local-gates/bobcat-gate/Bobcat.Supervisor.Tests.net10.0");
            environment[MonitorRunInfo.RunIdVariable].ShouldNotBeNullOrEmpty();
            environment[MonitorRunInfo.RunOwnerVariable].ShouldBe("supervisor");
        }
    }

    [Fact]
    public async Task without_a_run_tag_the_process_environment_is_honoured_exactly_as_before()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, "local-gates/bobcat-gate");

        var sink = new RecordingSink();
        var factory = onePassingTest();
        var supervisor = new Supervisor(factory)
        {
            PublishToMonitor = true,
            MonitorSink = sink
        };

        await supervisor.Run();

        sink.Events.First().ShouldBeOfType<RunStarted>().Tag.ShouldBe("local-gates/bobcat-gate");

        // Not stamped: the workers inherit the process's own variable, as they always have.
        foreach (var worker in factory.Launched)
        {
            worker.Launch.Environment.ShouldNotBeNull()
                .ContainsKey(MonitorRunInfo.RunTagVariable).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task two_supervisors_in_one_process_publish_under_their_own_tags()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, "local-gates/bobcat-gate");

        // One sink for both, as a gate sharing its targets across units would have.
        var sink = new RecordingSink();

        var first = new Supervisor(onePassingTest())
        {
            PublishToMonitor = true, MonitorSink = sink, RunTag = "local-gates/bobcat-gate/A.net10.0"
        };
        var second = new Supervisor(onePassingTest())
        {
            PublishToMonitor = true, MonitorSink = sink, RunTag = "local-gates/bobcat-gate/B.net9.0"
        };

        await first.Run();
        await second.Run();

        var started = sink.Events.OfType<RunStarted>().ToArray();
        started.Length.ShouldBe(2);
        started.Select(s => s.Tag).ShouldBe(
            ["local-gates/bobcat-gate/A.net10.0", "local-gates/bobcat-gate/B.net9.0"]);
        started[0].RunId.ShouldNotBe(started[1].RunId);
    }

    [Fact]
    public void the_decorator_rewrites_run_started_only_and_passes_everything_else_through()
    {
        var inner = new RecordingSink();
        var sink = new TagRewritingSink(inner, "plan/node/unit");

        var runId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        sink.Post(new RunStarted(runId, "suite", "repo", "main", "supervised", at, 3, Tag: "from-the-environment"));
        sink.Post(new RunHeartbeat(runId, at));
        sink.Post(new RunFinished(runId, 0, 3, 0, 0, 0, at));

        inner.Events.Count.ShouldBe(3);

        var started = inner.Events[0].ShouldBeOfType<RunStarted>();
        started.Tag.ShouldBe("plan/node/unit");
        started.RunId.ShouldBe(runId);
        started.Suite.ShouldBe("suite");
        started.TotalScenarios.ShouldBe(3);

        inner.Events[1].ShouldBeOfType<RunHeartbeat>().RunId.ShouldBe(runId);
        inner.Events[2].ShouldBeOfType<RunFinished>().RunId.ShouldBe(runId);
    }
}
