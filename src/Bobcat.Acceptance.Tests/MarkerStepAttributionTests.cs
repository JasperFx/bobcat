using Bobcat;
using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #304, end to end in a real compilation: a <c>[BobcatStep]</c> helper called under a
/// marker comment reports WHICH comment it ran under, and the narrative itself reaches the wire.
/// </summary>
/// <remarks>
/// <para>
/// This is the only test in the repository where an interceptor actually intercepts. Everything
/// else asserts the generated <i>text</i>, which cannot tell you that the call was replaced, that
/// the index survived into the emitted argument, or that the runtime bounds check let it through.
/// The project opts into <c>Bobcat.Generated</c> in its csproj for exactly this — the package
/// carries that property for consumers, and a ProjectReference does not.
/// </para>
/// <para>
/// <b>Declared is not executed, and the assertions keep saying so.</b> The narrative is read off
/// <c>ScenarioStarted</c>; the verdicts and durations are read off the steps. Nothing here lets a
/// declared step acquire a verdict of its own.
/// </para>
/// </remarks>
[BobcatFeature("Marker step attribution")]
public class MarkerStepAttributionTests
{
    [BobcatStep("the events are published", Keyword = "Given")]
    internal void PublishEvents()
    {
    }

    [BobcatStep("the daemon is running", Keyword = "When")]
    internal void StartDaemon()
    {
    }

    [BobcatStep("every aggregate matches", Keyword = "Then")]
    internal void CheckAggregates()
    {
    }

    [Fact]
    public void a_step_attributes_itself_to_the_comment_it_ran_under()
    {
        var sink = new RecordingSink();
        using var recording = ScenarioRecorder.Begin(
            "Marker step attribution",
            "a step attributes itself to the comment it ran under",
            sink,
            Guid.NewGuid());

        // Given the events are published
        PublishEvents();

        // When the daemon is running
        StartDaemon();

        // Then every aggregate matches
        CheckAggregates();

        sink.Events.OfType<StepStarted>().Select(x => x.DeclaredStepNumber).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void a_call_before_the_narrative_starts_belongs_to_no_sentence()
    {
        var sink = new RecordingSink();
        using var recording = ScenarioRecorder.Begin(
            "Marker step attribution",
            "a call before the narrative starts belongs to no sentence",
            sink,
            Guid.NewGuid());

        // Not a marker: this call runs before any keyword comment in the method.
        PublishEvents();

        // Given the events are published
        PublishEvents();

        sink.Events.OfType<StepStarted>().Select(x => x.DeclaredStepNumber).ShouldBe([null, 1]);
    }

    [Fact]
    public void the_narrative_reaches_the_wire_with_the_announcement()
    {
        var sink = new RecordingSink();

        // Given a scenario whose steps are declared in comments
        using var recording = ScenarioRecorder.Begin(
            "Marker step attribution",
            "the narrative reaches the wire with the announcement",
            sink,
            Guid.NewGuid());

        // Then the sentences arrive before anything has run
        var started = sink.Events.OfType<ScenarioStarted>().ShouldHaveSingleItem();
        started.DeclaredSteps.ShouldNotBeNull();
        started.DeclaredSteps.Select(x => $"{x.Keyword} {x.Text}").ShouldBe(
        [
            "Given a scenario whose steps are declared in comments",
            "Then the sentences arrive before anything has run"
        ]);
        sink.Events.OfType<StepStarted>().ShouldBeEmpty();
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
