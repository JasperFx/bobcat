using System.Reflection;
using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// Issue #110: the run bracket a marker-step adapter opens a scenario inside.
/// </summary>
/// <remarks>
/// Every test here pins something the first hand-rolled adapter got wrong, and each of those was
/// invisible in a green suite — a verdict that was always <c>CleanPass</c>, a run that could not be
/// attributed to whatever asked for it, and a bracket published by a process that did not own it.
/// </remarks>
[Collection("marker-step-run")]
public class MarkerStepRunTests : IDisposable
{
    private readonly RecordingSink _sink = new();

    public MarkerStepRunTests() => MarkerStepRun.Reset(_sink);

    public void Dispose() => MarkerStepRun.Reset();

    private static MethodInfo Method(string name)
        => typeof(SampleSpecs).GetMethod(name)!;

    private ScenarioRecorder.Recording begin(string method = nameof(SampleSpecs.the_daemon_catches_up))
    {
        MarkerStepRun.StartForTesting(new MonitorRunInfo(Guid.NewGuid(), "Suite", "/repo", "main", "xunit"));
        return MarkerStepRun.BeginScenario(Method(method), "xunit");
    }

    [Fact]
    public void a_failing_test_is_published_as_a_failure()
    {
        // The defect this whole package exists for. An adapter that never sets the verdict
        // publishes CleanPass for a red test, and nothing downstream can tell.
        begin();

        MarkerStepRun.EndScenario(ScenarioVerdict.Failed("Xunit.Sdk.TrueException", "deliberate"));

        var finished = _sink.Events.OfType<ScenarioFinished>().ShouldHaveSingleItem();
        finished.Outcome.ShouldBe("Failed");
        finished.ErrorMessage.ShouldBe("Xunit.Sdk.TrueException: deliberate");
    }

    [Fact]
    public void a_passing_test_is_published_as_a_clean_pass()
    {
        begin();

        MarkerStepRun.EndScenario(ScenarioVerdict.Passed);

        var finished = _sink.Events.OfType<ScenarioFinished>().ShouldHaveSingleItem();
        finished.Outcome.ShouldBe("CleanPass");
        finished.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public void a_skipped_test_publishes_no_verdict_at_all()
    {
        // It asserted nothing. CleanPass would be the same lie as CleanPass for a failure, and
        // RunOutcome has no word for "did not happen".
        begin();

        MarkerStepRun.EndScenario(ScenarioVerdict.NotClaimed);

        _sink.Events.OfType<ScenarioFinished>().ShouldBeEmpty();
        ScenarioRecorder.Current.ShouldBeNull();
    }

    [Fact]
    public void the_identity_is_the_one_that_joins_evidence_to_a_slice()
    {
        // {Feature}/{Scenario}, with the feature coming from [BobcatFeature] and the scenario
        // from the method name. That string is the join, so it is worth pinning exactly.
        var recording = begin();

        recording.Uid.ShouldBe("Async daemon/the daemon catches up");
    }

    [Fact]
    public void a_class_without_the_attribute_falls_back_to_its_own_name()
    {
        MarkerStepRun.StartForTesting(new MonitorRunInfo(Guid.NewGuid(), "Suite", "/repo", "main", "xunit"));

        var recording = MarkerStepRun.BeginScenario(
            typeof(UnadornedSpecs).GetMethod(nameof(UnadornedSpecs.it_works))!, "xunit");

        recording.Uid.ShouldBe("UnadornedSpecs/it works");
    }

    [Fact]
    public void the_type_and_name_overload_produces_the_same_identity()
    {
        // TUnit hands out no MethodInfo, so this overload has to agree with the other one or the
        // two adapters would disagree about what a scenario is called.
        MarkerStepRun.StartForTesting(new MonitorRunInfo(Guid.NewGuid(), "Suite", "/repo", "main", "tunit"));

        var recording = MarkerStepRun.BeginScenario(
            typeof(SampleSpecs), nameof(SampleSpecs.the_daemon_catches_up), "tunit");

        recording.Uid.ShouldBe("Async daemon/the daemon catches up");
    }

    [Fact]
    public void the_run_carries_the_tag_that_asked_for_it()
    {
        // BOBCAT_RUN_TAG is how a run says which plan node it speaks for. An adapter that mints
        // its own RunId and drops the tag produces evidence nothing can attribute.
        var info = new MonitorRunInfo(Guid.NewGuid(), "DaemonTests", "/repo", "main", "xunit")
        {
            Tag = "event-modeling-wave-2/daemon"
        };

        MarkerStepRun.StartForTesting(info);

        var started = _sink.Events.OfType<RunStarted>().ShouldHaveSingleItem();
        started.Tag.ShouldBe("event-modeling-wave-2/daemon");
        started.RunId.ShouldBe(info.RunId);
        started.Repository.ShouldBe("/repo");
        started.Branch.ShouldBe("main");
    }

    [Fact]
    public void a_participant_does_not_publish_a_bracket_it_does_not_own()
    {
        // The supervisor owns the run. A worker posting its own RunStarted would overwrite the
        // owner's suite name and true scenario total — the rule MonitorPublishingObserver follows.
        MarkerStepRun.StartForTesting(
            new MonitorRunInfo(Guid.NewGuid(), "worker", "/repo", "main", "xunit") { HasExternalOwner = true });

        _sink.Events.OfType<RunStarted>().ShouldBeEmpty();
    }

    [Fact]
    public void the_run_bracket_closes_with_the_counts_it_observed()
    {
        MarkerStepRun.StartForTesting(new MonitorRunInfo(Guid.NewGuid(), "Suite", "/repo", "main", "xunit"));

        MarkerStepRun.BeginScenario(Method(nameof(SampleSpecs.the_daemon_catches_up)), "xunit");
        MarkerStepRun.EndScenario(ScenarioVerdict.Passed);

        MarkerStepRun.BeginScenario(Method(nameof(SampleSpecs.the_daemon_catches_up)), "xunit");
        MarkerStepRun.EndScenario(ScenarioVerdict.Failed("X", "boom"));

        MarkerStepRun.BeginScenario(Method(nameof(SampleSpecs.the_daemon_catches_up)), "xunit");
        MarkerStepRun.EndScenario(ScenarioVerdict.NotClaimed);

        MarkerStepRun.FinishForTesting();

        var finished = _sink.Events.OfType<RunFinished>().ShouldHaveSingleItem();
        finished.Passed.ShouldBe(1);
        finished.Failed.ShouldBe(1);
        // The skip counts as neither, which is the point of withdrawing it.
        finished.ExitCode.ShouldBe(1);
    }

    [Fact]
    public void ending_a_scenario_that_was_never_begun_is_silent()
    {
        // A [BobcatScenario] on a test the runner skipped outright still reaches After.
        Should.NotThrow(() => MarkerStepRun.EndScenario(ScenarioVerdict.Passed));

        _sink.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, null, "Failed")]
    [InlineData("Xunit.Sdk.TrueException", null, "Xunit.Sdk.TrueException")]
    [InlineData(null, "no shard", "no shard")]
    [InlineData("System.TimeoutException", "no shard", "System.TimeoutException: no shard")]
    public void a_failure_always_describes_itself(string? type, string? message, string expected)
        => ScenarioVerdict.Failed(type, message).Describe().ShouldBe(expected);

    [BobcatFeature("Async daemon")]
    public class SampleSpecs
    {
        public void the_daemon_catches_up() { }
    }

    public class UnadornedSpecs
    {
        public void it_works() { }
    }

    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = new();

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }
    }
}

/// <summary>
/// <see cref="MarkerStepRun"/> is process-wide state, so its tests cannot run beside each other.
/// </summary>
[CollectionDefinition("marker-step-run", DisableParallelization = true)]
public class MarkerStepRunCollection;
