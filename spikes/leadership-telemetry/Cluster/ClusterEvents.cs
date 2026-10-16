namespace Cluster;

/// <summary>Wolverine's <c>IWolverineEvent</c>, reduced to what the leadership suite observes.</summary>
public interface IClusterEvent;

public sealed record NodeStarted(int NodeNumber) : IClusterEvent;

public sealed record NodeStopped(int NodeNumber) : IClusterEvent;

public sealed record LeadershipAssumed(int NodeNumber) : IClusterEvent;

/// <summary>
/// One agent in one of the leader's evaluations: where it was, and where the leader put it. Wolverine's
/// <c>AgentAssignment</c> carries the same pair as <c>OriginalNode</c> / <c>AssignedNode</c>.
/// </summary>
public sealed record AgentAssignment(Uri Uri, int? OriginalNode, int? AssignedNode);

/// <summary>
/// Wolverine's <c>AgentAssignmentsChanged</c>: published by the leader after every evaluation, whether
/// or not it changed anything, on the leader's own background loop.
/// </summary>
public sealed record AgentAssignmentsChanged(
    int LeaderNode,
    int Evaluation,
    IReadOnlyList<int> Nodes,
    IReadOnlyList<AgentAssignment> Assignments,
    int Commands) : IClusterEvent;

/// <summary>Wolverine's <c>runtime.Tracker</c>: subscribe an observer to one node's events.</summary>
public sealed class ClusterTracker
{
    private readonly object _gate = new();
    private IObserver<IClusterEvent>[] _observers = [];

    public IDisposable Subscribe(IObserver<IClusterEvent> observer)
    {
        lock (_gate) _observers = [.. _observers, observer];
        return new Unsubscriber(this, observer);
    }

    internal void Publish(IClusterEvent e)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnNext(e);
            }
            catch
            {
                // An observer is never the reason the cluster misbehaves.
            }
        }
    }

    private sealed class Unsubscriber(ClusterTracker tracker, IObserver<IClusterEvent> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (tracker._gate) tracker._observers = tracker._observers.Where(x => x != observer).ToArray();
        }
    }
}
