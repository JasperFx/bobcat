namespace Cluster;

/// <summary>A node as the table has it: Wolverine's <c>WolverineNode</c> from <c>LoadAllNodesAsync</c>.</summary>
public sealed record StoredNode(Guid NodeId, int NodeNumber, IReadOnlyList<Uri> ActiveAgents);

/// <summary>
/// The shared database every node reads and writes: node registrations, the leadership lock and the
/// assignment rows. Wolverine's <c>runtime.Storage.Nodes</c>, in memory.
/// </summary>
public sealed class NodeStorage
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _nodes = new();
    private readonly Dictionary<Uri, Guid> _assignments = new();
    private Guid? _leader;
    private int _lastNumber;

    public int Register(Guid nodeId)
    {
        lock (_gate)
        {
            var number = ++_lastNumber;
            _nodes[nodeId] = number;
            return number;
        }
    }

    /// <summary>A node going away takes its rows and, if it held it, the leadership lock with it.</summary>
    public void Remove(Guid nodeId)
    {
        lock (_gate)
        {
            _nodes.Remove(nodeId);
            foreach (var uri in _assignments.Where(x => x.Value == nodeId).Select(x => x.Key).ToArray())
            {
                _assignments.Remove(uri);
            }

            if (_leader == nodeId) _leader = null;
        }
    }

    public bool TryTakeLeadership(Guid nodeId)
    {
        lock (_gate)
        {
            if (_leader is { } held && _nodes.ContainsKey(held)) return held == nodeId;

            _leader = nodeId;
            return true;
        }
    }

    public void AddAssignment(Guid nodeId, Uri agent)
    {
        lock (_gate) _assignments[agent] = nodeId;
    }

    /// <summary>Owner-scoped, like the GH-4407 fix: a node can only remove its own claim.</summary>
    public void RemoveAssignment(Guid nodeId, Uri agent)
    {
        lock (_gate)
        {
            if (_assignments.TryGetValue(agent, out var owner) && owner == nodeId) _assignments.Remove(agent);
        }
    }

    public IReadOnlyList<StoredNode> LoadAllNodes()
    {
        lock (_gate)
        {
            return _nodes
                .Select(n => new StoredNode(n.Key, n.Value,
                    _assignments.Where(a => a.Value == n.Key).Select(a => a.Key).OrderBy(x => x.ToString()).ToArray()))
                .OrderBy(x => x.NodeNumber)
                .ToArray();
        }
    }
}
