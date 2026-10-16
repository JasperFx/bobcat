using Bobcat;
using Bobcat.Xunit;
using Cluster;
using Compliance.Telemetry;
using Xunit;

namespace Compliance;

/// <summary>
/// The shape of Wolverine's <c>LeadershipElectionCompliance</c>
/// (<c>src/Testing/Wolverine.ComplianceTests/LeadershipElectionCompliance.cs</c>): an abstract base in a
/// class library, one subclass per persistence provider, the first node started in
/// <c>InitializeAsync</c>. The specifications keep Wolverine's test names, and their bodies are the same
/// waits, written as steps.
/// </summary>
public abstract class LeadershipElectionCompliance : IAsyncLifetime
{
    static LeadershipElectionCompliance()
    {
        // Once per process; registering the same instance again is one registration.
        SpecAttachment.RegisterFailureCapture(ClusterTranscriptCapture.Instance);
    }

    protected readonly ClusterSteps the;

    protected LeadershipElectionCompliance()
    {
        var options = new ClusterOptions();
        configureNode(options);
        the = new ClusterSteps(options);
    }

    /// <summary>Wolverine's <c>configureNode(WolverineOptions)</c>: the provider's half.</summary>
    protected abstract void configureNode(ClusterOptions options);

    public ValueTask InitializeAsync()
    {
        the.ANodeStarts();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => the.DisposeAsync();

    [BobcatSpec]
    public async Task the_only_known_node_is_automatically_the_leader()
    {
        await the.NodeLeads(1);
    }

    [BobcatSpec]
    public async Task the_original_node_knows_about_all_the_possible_agents()
    {
        await the.AgentsSettleAs("1=12");
    }

    [BobcatSpec]
    public async Task add_second_node_see_balanced_nodes()
    {
        the.ANodeStarts();

        await the.AgentsSettleAs("1=6, 2=6");
    }

    [BobcatSpec]
    public async Task spin_up_several_nodes_take_away_original_node()
    {
        await the.NodeLeads(1);
        the.ANodeStarts();
        the.ANodeStarts();
        the.ANodeStarts();
        await the.AgentsSettleAs("1=3, 2=3, 3=3, 4=3");

        await the.NodeStops(1);

        await the.ASurvivorLeads();
        await the.AgentsSettleAs("2=4, 3=4, 4=4");
    }

    [BobcatSpec]
    public async Task every_agent_runs_exactly_once_after_the_leader_dies_with_its_starts_in_flight()
    {
        await the.NodeLeads(1);

        // New nodes join and the leader starts rebalancing onto them -- then dies before those starts
        // have settled. The next leader inherits whatever landed.
        the.ANodeStarts();
        the.ANodeStarts();
        await the.NodeStops(1);

        await the.EveryAgentRunsOnce();
    }
}
