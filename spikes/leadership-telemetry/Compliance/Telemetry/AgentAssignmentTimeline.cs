using Bobcat.Engine;
using Cluster;

namespace Compliance.Telemetry;

/// <summary>
/// The cluster's agent-assignment history as a Bobcat report: one row per node event and per agent the
/// leader moved, with the runs of evaluations that changed nothing folded into one row each. Written out
/// only for a failed specification (<see cref="ReportVisibility.OnFailure"/>), capped at
/// <see cref="TableReport.DefaultMaxRows"/> rows with "…and N more".
/// </summary>
/// <remarks>
/// <b>An <see cref="IScenarioReport"/> rather than a <see cref="TableReport"/> subclass</b>, because its
/// rows arrive on other threads (see <see cref="AgentAssignmentTelemetry"/>). It builds a
/// <see cref="TableReport"/> from a snapshot the first time a renderer asks for its cells -- at the
/// scenario's close, on the test's side -- and keeps it, so the cap and the row stamping are still
/// <see cref="TableReport"/>'s.
/// </remarks>
public sealed class AgentAssignmentTimeline : IScenarioReport
{
    private readonly AgentAssignmentTelemetry _telemetry;
    private readonly Lazy<Grid> _grid;

    internal AgentAssignmentTimeline(AgentAssignmentTelemetry telemetry)
    {
        _telemetry = telemetry;
        _grid = new Lazy<Grid>(build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public AgentAssignmentTelemetry Telemetry => _telemetry;

    public string Title => "Agent assignments over time";

    public string? ShortTitle => "Assignments";

    public ReportVisibility Visibility => ReportVisibility.OnFailure;

    public IReadOnlyList<string> Columns => _grid.Value.Columns;

    public IReadOnlyList<CellResult> Cells => _grid.Value.Cells;

    public int SuppressedRows => _grid.Value.SuppressedRows;

    private Grid build()
    {
        var grid = new Grid();
        var quiet = 0;
        TimeSpan? quietSince = null;

        void flushQuiet()
        {
            if (quiet == 0) return;
            grid.Add(quietSince, null, null, null, $"{quiet} evaluation(s), no changes");
            quiet = 0;
            quietSince = null;
        }

        foreach (var (at, e) in _telemetry.Snapshot())
        {
            switch (e)
            {
                case AgentAssignmentsChanged { Commands: 0 }:
                    quietSince ??= at;
                    quiet++;
                    break;

                case AgentAssignmentsChanged changed:
                    flushQuiet();
                    foreach (var agent in changed.Assignments.Where(x => x.OriginalNode != x.AssignedNode))
                    {
                        grid.Add(at, changed.LeaderNode, changed.Evaluation, agent.Uri, AgentAssignmentTelemetry.Describe(agent));
                    }

                    break;

                case NodeStarted started:
                    flushQuiet();
                    grid.Add(at, null, null, null, $"node {started.NodeNumber} started");
                    break;

                case NodeStopped stopped:
                    flushQuiet();
                    grid.Add(at, null, null, null, $"node {stopped.NodeNumber} stopped");
                    break;

                case LeadershipAssumed assumed:
                    flushQuiet();
                    grid.Add(at, assumed.NodeNumber, null, null, $"node {assumed.NodeNumber} assumed leadership");
                    break;
            }
        }

        flushQuiet();
        return grid;
    }

    private sealed class Grid : TableReport
    {
        public override string Title => "Agent assignments over time";

        public void Add(TimeSpan? at, int? leader, int? evaluation, Uri? agent, string change)
            => Row(
                ("at", at is { } t ? $"{t.TotalSeconds:0.000}s" : ""),
                ("leader", leader?.ToString() ?? ""),
                ("evaluation", evaluation?.ToString() ?? ""),
                ("agent", agent?.ToString() ?? ""),
                ("change", change));
    }
}
