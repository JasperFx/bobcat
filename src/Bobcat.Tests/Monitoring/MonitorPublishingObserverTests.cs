using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

public class MonitorPublishingObserverTests
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

    private static readonly MonitorRunInfo info =
        new(Guid.NewGuid(), "TestSuite", "/repo", "main", "in-process");

    /// <summary>Fails on the first attempt, passes on the second.</summary>
    private static readonly Dictionary<string, int> attempts = new();

    public class MonitoredFixture : Fixture;

    private static FeatureDefinition feature()
    {
        var key = Guid.NewGuid().ToString();
        attempts[key] = 0;

        var clean = new ScenarioDefinition("clean pass", [], (_, plan) =>
        {
            plan.Add(new DelegateExecutionStep("step-1", StepKind.Given, "a clean step",
                (_, _, _) => Task.CompletedTask));
        });

        var flaky = new ScenarioDefinition("flaky", ["retry(2)"], (_, plan) =>
        {
            plan.Add(new DelegateExecutionStep("step-1", StepKind.Then, "a flaky step",
                (_, result, _) =>
                {
                    if (++attempts[key] == 1) result.MarkFailed();
                    return Task.CompletedTask;
                }));
        });

        return new FeatureDefinition("Monitored", typeof(MonitoredFixture), [clean, flaky]);
    }

    [Fact]
    public async Task a_full_run_streams_the_expected_event_sequence()
    {
        var sink = new RecordingSink();
        var runner = new BobcatRunner
        {
            SuppressConsoleOutput = true,
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 }
        };
        runner.AddFeature(feature());
        runner.AddObserver(new MonitorPublishingObserver(sink, info));

        var results = await runner.RunAll();
        results.ExitCode.ShouldBe(0);

        var events = sink.Events;

        // The bracket: RunStarted first with the filtered total, RunFinished last with the
        // honest counts (pass-on-retry never folded into clean passes).
        var started = events.First().ShouldBeOfType<RunStarted>();
        started.RunId.ShouldBe(info.RunId);
        started.Suite.ShouldBe("TestSuite");
        started.TotalScenarios.ShouldBe(2);

        var finished = events.Last().ShouldBeOfType<RunFinished>();
        finished.ExitCode.ShouldBe(0);
        finished.Passed.ShouldBe(1);
        finished.PassedOnRetry.ShouldBe(1);
        finished.Failed.ShouldBe(0);

        // Steps carry the scenario's uid — the same "{Feature}/{Scenario}" identity everywhere.
        events.OfType<StepStarted>().ShouldContain(e => e.Uid == "Monitored/clean pass" && e.Kind == "Given");
        events.OfType<StepFinished>().ShouldContain(e => e.Uid == "Monitored/clean pass" && e.Status == "success");

        // The flaky scenario: attempt 1 fails, a retry is announced, attempt 2 runs, and the
        // terminal event reports PassOnRetry with both attempts.
        var flakyStarts = events.OfType<ScenarioStarted>().Where(e => e.Scenario == "flaky").ToArray();
        flakyStarts.Select(e => e.Attempt).ShouldBe([1, 2]);

        var retry = events.OfType<RetryScheduled>().Single();
        retry.Uid.ShouldBe("Monitored/flaky");
        retry.NextAttempt.ShouldBe(2);
        retry.Disposition.ShouldBe("RetryInProcess");

        var flakyFinished = events.OfType<ScenarioFinished>().Single(e => e.Uid == "Monitored/flaky");
        flakyFinished.Outcome.ShouldBe("PassOnRetry");
        flakyFinished.Attempts.ShouldBe(2);

        // The failed first attempt's step reports failed status before the retry.
        events.OfType<StepFinished>().ShouldContain(e => e.Uid == "Monitored/flaky" && e.Status == "failed");
    }

    [Fact]
    public async Task heartbeats_flow_between_run_started_and_run_finished_and_then_stop()
    {
        var sink = new RecordingSink();
        await using var observer = new MonitorPublishingObserver(
            sink, info, heartbeatInterval: TimeSpan.FromMilliseconds(25));

        observer.RunStarted(1);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!sink.Events.OfType<RunHeartbeat>().Any() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        sink.Events.OfType<RunHeartbeat>().ShouldNotBeEmpty();

        observer.RunFinished(new SuiteResults());
        var countAtFinish = sink.Events.OfType<RunHeartbeat>().Count();

        await Task.Delay(150);
        sink.Events.OfType<RunHeartbeat>().Count().ShouldBe(countAtFinish);
    }

    [Fact]
    public async Task a_participant_run_suppresses_the_bracket_but_still_streams_scenarios()
    {
        // A supervisor's worker: shares the owner's RunId, but the bracket (RunStarted,
        // heartbeats, RunFinished) is the owner's to publish — the first worker finishing
        // must not mark the whole shared run finished with its own partial counts.
        var sink = new RecordingSink();
        var runner = new BobcatRunner
        {
            SuppressConsoleOutput = true,
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 }
        };
        runner.AddFeature(feature());
        runner.AddObserver(new MonitorPublishingObserver(
            sink, info with { HasExternalOwner = true }, heartbeatInterval: TimeSpan.FromMilliseconds(25)));

        var results = await runner.RunAll();
        results.ExitCode.ShouldBe(0);

        sink.Events.OfType<RunStarted>().ShouldBeEmpty();
        sink.Events.OfType<RunFinished>().ShouldBeEmpty();
        sink.Events.OfType<RunHeartbeat>().ShouldBeEmpty();

        // The scenario and step stream is the whole point of a participant.
        sink.Events.OfType<ScenarioStarted>().ShouldNotBeEmpty();
        sink.Events.OfType<StepFinished>().ShouldNotBeEmpty();
        sink.Events.OfType<ScenarioFinished>().ShouldNotBeEmpty();
        sink.Events.ShouldAllBe(e => e.RunId == info.RunId);
    }

    [Fact]
    public void discover_reads_the_grouping_pair_from_the_environment()
    {
        var runId = Guid.NewGuid();
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunIdVariable, runId.ToString());
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunOwnerVariable, "supervisor");
        try
        {
            var discovered = MonitorRunInfo.Discover("mtp-host");
            discovered.RunId.ShouldBe(runId);
            discovered.HasExternalOwner.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(MonitorRunInfo.RunIdVariable, null);
            Environment.SetEnvironmentVariable(MonitorRunInfo.RunOwnerVariable, null);
        }

        // BOBCAT_RUN_ID alone pins identity without ceding the bracket — a standalone run
        // with a caller-chosen id still announces and closes itself.
        MonitorRunInfo.Discover("in-process").HasExternalOwner.ShouldBeFalse();
    }

    [Fact]
    public async Task run_finished_fires_even_when_preflight_fails()
    {
        var sink = new RecordingSink();
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(feature());
        runner.Preflight.Add("doomed", _ => throw new InvalidOperationException("no database"));
        runner.AddObserver(new MonitorPublishingObserver(sink, info));

        var results = await runner.RunAll();
        results.PreflightFailure.ShouldNotBeNull();

        sink.Events.First().ShouldBeOfType<RunStarted>();
        var finished = sink.Events.Last().ShouldBeOfType<RunFinished>();
        finished.ExitCode.ShouldBe(2);
    }
}
