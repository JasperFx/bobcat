using Bobcat.Xunit.Samples.Grammars;
using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// One specification per step-helper shape on <c>docs/marker-steps.md</c> that is not already a sample:
/// a value placeholder, a poll that converges, a step that gathers and then stops, and a projected
/// assertion inside a loop. The agents one fails on purpose; the rest are green.
/// </summary>
[BobcatFeature("Step helpers"), BobcatScenario]
public class StepHelperSpecs
{
    [Fact]
    public async Task the_queue_drains()
    {
        var queue = new QueueSteps();

        queue.ItemsAreEnqueued(4);
        queue.TheWorkersStart();
        await queue.OutstandingBecomes(0);
    }

    [Fact]
    public void an_agent_running_twice_is_reported_before_the_scenario_stops()
    {
        var cluster = new ClusterSteps();

        cluster.AgentsAreAssigned("billing, shipping");
        cluster.NodeRuns("node 1", "billing, shipping");
        cluster.NodeRuns("node 2", "billing");
        cluster.TheAgentsSettle();
    }

    [Fact]
    public void every_event_is_at_version_one()
    {
        var events = Enumerable.Range(1, 60).Select(i => new VersionedEvent(i, Version: 1)).ToList();

        // #region assertion-in-a-loop
        // 60 steps, one per event
        foreach (var e in events)
        {
            e.Version.ShouldBe(1);
        }

        // one step
        events.ShouldAllBe(e => e.Version == 1);
        // #endregion assertion-in-a-loop
    }

    public record VersionedEvent(int Sequence, int Version);
}
