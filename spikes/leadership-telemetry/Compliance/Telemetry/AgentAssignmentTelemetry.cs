using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Bobcat.Engine;
using Cluster;

namespace Compliance.Telemetry;

/// <summary>
/// Everything the cluster said about agent assignments during one specification, gathered as it happens
/// and turned into Bobcat's model only when somebody reads it -- which, for a passing specification, is
/// nobody.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it buffers instead of writing straight into a report.</b> The cluster publishes on each node's
/// own background loop (Wolverine's <c>runtime.Tracker</c> does the same). <see cref="Bobcat.SpecLog"/>,
/// <see cref="Bobcat.SpecReport"/> and <see cref="ScenarioReports.Current"/> are <c>AsyncLocal</c>, so from
/// that thread there is no scenario to write to: the call is a silent no-op. And a
/// <see cref="TableReport"/> is a plain list, not safe to append to from five threads while the
/// renderer reads it. So the observer only enqueues, and <see cref="AgentAssignmentTimeline"/> snapshots
/// the queue on the test's side.
/// </para>
/// <para>
/// <b>Bounded.</b> A leader publishes after every evaluation, changed or not -- twenty a second here, one a
/// second in Wolverine -- and a stuck test keeps going until its timeout. The newest
/// <see cref="MaxEvents"/> are kept and the rest counted, so the transcript says how much it lost.
/// </para>
/// </remarks>
public sealed class AgentAssignmentTelemetry : IObserver<IClusterEvent>
{
    public const int MaxEvents = 5_000;

    private readonly ConcurrentQueue<Observed> _events = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<IDisposable> _subscriptions = new();
    private int _dropped;

    public AgentAssignmentTelemetry()
    {
        Timeline = new AgentAssignmentTimeline(this);
    }

    /// <summary>The scenario report this telemetry renders as. One per specification.</summary>
    public AgentAssignmentTimeline Timeline { get; }

    /// <summary>Wolverine's <c>new XUnitEventObserver(host, _output)</c>.</summary>
    public void Watch(ClusterNode node)
    {
        lock (_subscriptions) _subscriptions.Add(node.Tracker.Subscribe(this));
    }

    /// <summary>
    /// Put <see cref="Timeline"/> on the scenario in progress. Called from the test's own async context;
    /// a no-op outside a specification, and when it is already there.
    /// </summary>
    public void AttachToScenario()
    {
        if (ScenarioReports.Current is { } sink && !sink.Reports.Contains(Timeline)) sink.AttachReport(Timeline);
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public void OnNext(IClusterEvent value)
    {
        _events.Enqueue(new Observed(_clock.Elapsed, value));

        while (_events.Count > MaxEvents && _events.TryDequeue(out _))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public void OnError(Exception error)
    {
    }

    public void OnCompleted()
    {
    }

    /// <summary>What has been observed so far, oldest first.</summary>
    public IReadOnlyList<Observed> Snapshot() => _events.ToArray();

    public int Dropped => _dropped;

    /// <summary>
    /// Everything, in the format Wolverine's <c>XUnitEventObserver</c> writes to test output today, with
    /// the time added -- including the evaluations that changed nothing, which the timeline folds away.
    /// </summary>
    public string Transcript()
    {
        var builder = new StringBuilder();
        if (_dropped > 0) builder.AppendLine($"({_dropped} earlier events not kept; the newest {MaxEvents} follow)");

        foreach (var (at, e) in Snapshot())
        {
            var stamp = $"{at.TotalSeconds,7:0.000}s";
            switch (e)
            {
                case AgentAssignmentsChanged changed:
                    builder.AppendLine(
                        $"{stamp} Node {changed.LeaderNode}: evaluation {changed.Evaluation} for known nodes {string.Join(", ", changed.Nodes)}");
                    if (changed.Commands == 0) builder.AppendLine("          No assignment changes detected");

                    foreach (var agent in changed.Assignments)
                    {
                        builder.AppendLine($"          * {agent.Uri} {Describe(agent)}");
                    }

                    break;

                default:
                    builder.AppendLine($"{stamp} {e}");
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>One agent's line in an evaluation, worded as <c>XUnitEventObserver</c> words it.</summary>
    public static string Describe(AgentAssignment agent) => agent switch
    {
        { AssignedNode: null } => "is not assigned",
        { OriginalNode: null } => $"assigned to node {agent.AssignedNode}",
        _ when agent.OriginalNode == agent.AssignedNode => $"is unchanged on node {agent.AssignedNode}",
        _ => $"reassigned from node {agent.OriginalNode} to node {agent.AssignedNode}"
    };

    public sealed record Observed(TimeSpan At, IClusterEvent Event);
}
