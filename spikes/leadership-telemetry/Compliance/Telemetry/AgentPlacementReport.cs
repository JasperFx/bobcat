using Bobcat;
using Bobcat.Engine;
using Cluster;

namespace Compliance.Telemetry;

/// <summary>
/// Where every agent was at the moment a wait gave up: which node its assignment row names against which
/// nodes are actually running it. Replaces the "According to the database… / According to the runtimes…"
/// text Wolverine's <c>AssignmentWaiter</c> puts in its <c>TimeoutException</c>, as one grid where the
/// agents the two disagree about are the red rows.
/// </summary>
/// <remarks>
/// Built on the test's own side, inside the step that timed out, so this one is an ordinary
/// <see cref="TableReport"/> -- the simple case, next to <see cref="AgentAssignmentTimeline"/>'s
/// background one.
/// </remarks>
public sealed class AgentPlacementReport : TableReport
{
    public override string Title => "Agent placement: assignment rows against running agents";

    public override string? ShortTitle => "Placement";

    /// <summary>
    /// Replace this scenario's placement report with the cluster as it is now. A no-op outside a
    /// specification. Replaces rather than appends, so the report shows the LAST wait that gave up.
    /// </summary>
    public static void Capture(ClusterNetwork network)
    {
        if (!SpecReport.IsRecording) return;

        var report = new AgentPlacementReport();
        var rows = network.Storage.LoadAllNodes()
            .SelectMany(n => n.ActiveAgents.Select(a => (Agent: a, n.NodeNumber)))
            .ToDictionary(x => x.Agent, x => x.NodeNumber);
        var nodes = network.LiveNodes;

        foreach (var agent in ClusterNetwork.AllAgents)
        {
            var runners = nodes.Where(n => n.RunningAgents().Contains(agent)).Select(n => n.Number).ToArray();
            int? row = rows.TryGetValue(agent, out var named) ? named : null;

            var running = runners.Length == 0 ? "nowhere" : string.Join(", ", runners);
            var healthy = runners.Length == 1 && row == runners[0];

            report.Row([
                new CellResult("agent", ResultStatus.ok, agent.ToString()),
                new CellResult("row names", ResultStatus.ok, row?.ToString() ?? "no row"),
                new CellResult("running on", ResultStatus.ok, running),
                healthy
                    ? new CellResult("verdict", ResultStatus.success, "one copy, row agrees")
                    : new CellResult("verdict", ResultStatus.failed)
                    {
                        Expected = row is { } n ? $"running once, on node {n}" : "running once, with a row",
                        Actual = runners.Length switch
                        {
                            0 => "running nowhere",
                            1 => $"running on node {runners[0]}",
                            _ => $"running {runners.Length} times, on nodes {running}"
                        }
                    }
            ]);
        }

        SpecReport.Attach(report);
    }
}
