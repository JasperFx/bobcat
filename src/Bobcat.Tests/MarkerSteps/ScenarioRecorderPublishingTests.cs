using Bobcat;
using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// Issue #110: a marker step is only useful if somebody can see it. These pin the wire events a
/// scenario emits, because the console builds its step list from <c>StepStarted</c> and from
/// nothing else — a step that is recorded in memory and never published does not exist.
/// </summary>
public class ScenarioRecorderPublishingTests : IDisposable
{
    private const string Uid = "Async daemon/the daemon catches up";

    private readonly RecordingSink _sink = new();
    private readonly Guid _runId = Guid.NewGuid();

    public void Dispose() => DeclaredSteps.Clear();

    [Fact]
    public void a_step_is_announced_when_it_opens_not_when_it_ends()
    {
        // The distinction the whole feature rests on: a watcher looking at a run in flight needs
        // the step that is currently taking the time, which is the one that has not finished.
        using var recording = ScenarioRecorder.Begin("Async daemon", "the daemon catches up", _sink, _runId);

        var step = ScenarioRecorder.Step("Given", "the events are published");

        _sink.Events.OfType<StepStarted>().ShouldHaveSingleItem().Text.ShouldBe("the events are published");
        _sink.Events.OfType<StepFinished>().ShouldBeEmpty();

        step.Dispose();

        _sink.Events.OfType<StepFinished>().ShouldHaveSingleItem().Status.ShouldBe("Passed");
    }

    [Fact]
    public void a_failed_step_reports_its_own_verdict()
    {
        using var recording = ScenarioRecorder.Begin("Async daemon", "the daemon catches up", _sink, _runId);

        var step = ScenarioRecorder.Step("When", "the daemon is started");
        ((IStepHandle)step).Fail(new InvalidOperationException("no shard"));
        step.Dispose();

        var finished = _sink.Events.OfType<StepFinished>().ShouldHaveSingleItem();
        finished.Status.ShouldBe("Failed");
        finished.ErrorMessage.ShouldBe("no shard");
    }

    [Fact]
    public void the_disposal_that_follows_a_failure_does_not_overwrite_it()
    {
        // Track() fails the step and then its finally disposes it, so both arrive for one step.
        using var recording = ScenarioRecorder.Begin("Async daemon", "the daemon catches up", _sink, _runId);

        var step = ScenarioRecorder.Step("When", "the daemon is started");
        ((IStepHandle)step).Fail(new InvalidOperationException("no shard"));
        step.Dispose();
        step.Dispose();

        _sink.Events.OfType<StepFinished>().ShouldHaveSingleItem().Status.ShouldBe("Failed");
    }

    [Fact]
    public void steps_are_numbered_within_the_scenario()
    {
        using var recording = ScenarioRecorder.Begin("Async daemon", "the daemon catches up", _sink, _runId);

        ScenarioRecorder.Step("Given", "one").Dispose();
        ScenarioRecorder.Step("When", "two").Dispose();

        _sink.Events.OfType<StepStarted>().Select(x => x.StepNumber).ShouldBe([1, 2]);
        _sink.Events.OfType<StepStarted>().Select(x => x.StepId).ShouldBe(["s1", "s2"]);
    }

    [Fact]
    public void the_declared_step_count_is_announced_before_anything_runs()
    {
        // Known up front precisely because the comments were read at compile time — this is what
        // a marker-comment spec buys that a purely observed one cannot.
        DeclaredSteps.Register(Uid,
            new DeclaredStep("Given", "the events are published", 11),
            new DeclaredStep("When", "the daemon is started", 14),
            new DeclaredStep("Then", "every aggregate matches", 17));

        using var recording = ScenarioRecorder.Begin("Async daemon", "the daemon catches up", _sink, _runId);

        _sink.Events.OfType<ScenarioStarted>().ShouldHaveSingleItem().TotalSteps.ShouldBe(3);
        recording.Declared.Select(x => x.ToString()).ShouldBe(
            ["Given the events are published", "When the daemon is started", "Then every aggregate matches"]);
    }

    [Fact]
    public void a_scenario_that_declares_nothing_announces_no_count()
    {
        // Null, not zero: an older publisher and a scenario with no comments must not be confused
        // with one that genuinely has no steps.
        using var recording = ScenarioRecorder.Begin("Async daemon", "the daemon catches up", _sink, _runId);

        _sink.Events.OfType<ScenarioStarted>().ShouldHaveSingleItem().TotalSteps.ShouldBeNull();
        recording.Declared.ShouldBeEmpty();
    }

    [Fact]
    public void a_step_outside_any_scenario_is_silent()
    {
        // Decorated helpers are called from plenty of places that are not specifications.
        ScenarioRecorder.Step("Given", "no scenario is open").Dispose();

        _sink.Events.ShouldBeEmpty();
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
}
