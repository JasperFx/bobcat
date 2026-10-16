using Bobcat;
using Cluster;
using Compliance;

namespace Providers;

// Exactly Wolverine's shape: one bodyless subclass per provider, the provider's half in configureNode.

[BobcatFeature("In-memory leader election")]
public class InMemoryLeaderElection : LeadershipElectionCompliance
{
    protected override void configureNode(ClusterOptions options)
    {
    }
}

// Red on purpose: this provider's leader forgets the stop when it moves an agent between live nodes, so
// the specifications that rebalance fail, and their failure output is the worked example.
[BobcatFeature("Lossy-rebalance leader election")]
public class LossyRebalanceLeaderElection : LeadershipElectionCompliance
{
    protected override void configureNode(ClusterOptions options) => options.ForgetStopOnReassign = true;
}
