using Bobcat.Monitoring;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The supervisor as the run's monitor-facing owner: one bracket for the whole run, and the
/// grouping environment (<c>BOBCAT_RUN_ID</c> + <c>BOBCAT_RUN_OWNER</c>) on every worker
/// launch, so a supervised suite is one dashboard card instead of one per worker process.
/// </summary>
/// <remarks>
/// Serialized into its own collection and restoring both variables to the value they had, for the
/// reason <c>MonitorRunInfoTests</c> gives: these tests mutate process-wide state, and a developer
/// running this suite from inside an agent session genuinely has <c>CLAUDE_CODE_SESSION_ID</c> set.
/// </remarks>
[Collection("supervisor-monitor-env")]
public class SupervisorMonitorPublishingTests : IDisposable
{
    private readonly string? _previousSession
        = Environment.GetEnvironmentVariable(MonitorRunInfo.SessionVariable);

    private readonly string? _previousCommand
        = Environment.GetEnvironmentVariable(MonitorRunInfo.RunCommandVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, _previousSession);
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, _previousCommand);
    }

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
        Environment.SetEnvironmentVariable(
            MonitorRunInfo.SessionVariable, "session_that_launched_the_supervisor");

        // Cleared, because since #401 a command suppresses the session — so leaving an ambient
        // BOBCAT_RUN_COMMAND in place would make the assertion below read null and look like the
        // session was never stamped.
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, null);

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

        // Issue #389: a supervised run stamps the session that launched it. Its workers inherit the
        // variable for free — they are launched with this environment — but only the bracket owner
        // publishes run_started, so this is the one place it is read.
        //
        // The variable is SET by this test rather than mirrored out of the ambient environment, and
        // that is the whole point of the arrangement above. Mirrored, the assertion read
        // `Session.ShouldBe(<the same variable>)`, which on CI — where nothing sets it — compares
        // null to null and passes however the publisher behaves. Proved by deleting the wiring:
        // with the variable unset the test still passed, and it failed only on a developer's
        // machine, where an agent session happens to set it. A test that can only catch a bug on
        // one of the two machines that run it is not pinning anything.
        started.Session.ShouldBe("session_that_launched_the_supervisor");

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

    /// <summary>
    /// Issue #401's rule, through the supervisor's publisher: a run that carries a command carries
    /// no session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule lives on <c>MonitorRunInfo.Session</c> as a getter that consults <c>Command</c>, so
    /// it holds for every publisher by construction rather than by each one remembering. This pins
    /// it at the one altitude where that construction could be undone without any existing test
    /// noticing: <c>SupervisorRunPublisher</c> builds its own <c>MonitorRunInfo</c> through a
    /// <c>with</c> expression, and reads <c>_info.Session</c> and <c>_info.Command</c> as two
    /// separate arguments to <c>RunStarted</c>. Nothing stops a future edit there from passing a
    /// session it fetched some other way.
    /// </para>
    /// <para>
    /// It matters here specifically because a supervised run is the shape most likely to be
    /// commanded *and* launched from an agent's terminal at once — a console asks a resident runner
    /// for a slice's specs, the suite is big enough to be supervised, and the runner has held that
    /// terminal's session since it started. That is exactly the case #401 was filed about.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task a_commanded_supervised_run_carries_the_command_and_no_session()
    {
        // Both set, which is the real arrangement: the runner inherited the session when a person
        // started it, and the command is this button press.
        Environment.SetEnvironmentVariable(
            MonitorRunInfo.SessionVariable, "session_that_launched_the_runner");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, "cmd-from-the-console");

        var sink = new RecordingSink();
        var supervisor = new Supervisor(threeTests())
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 },
            PublishToMonitor = true,
            MonitorSink = sink
        };

        await supervisor.Run();

        var started = sink.Events.First().ShouldBeOfType<RunStarted>();

        started.Command.ShouldBe(
            "cmd-from-the-console",
            "the command is the true answer to who asked for this run");

        started.Session.ShouldBeNull(
            "a resident runner holds its launching session for life, so stamping it here would "
            + "claim every button press a person made");
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
