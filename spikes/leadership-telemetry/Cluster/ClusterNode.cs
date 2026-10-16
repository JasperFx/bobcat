using System.Collections.Concurrent;

namespace Cluster;

/// <summary>What differs between "providers" of the reproduction. Wolverine's is the persistence choice.</summary>
public sealed class ClusterOptions
{
    /// <summary>How often a node heartbeats, and a leader evaluates. Wolverine's <c>CheckAssignmentPeriod</c>.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The planted defect: when the leader moves an agent from one live node to another it sends the
    /// start and forgets the stop. The row follows the start, so the table reads as healthy while the
    /// agent runs twice -- the GH-3698 / GH-4407 family of bug, and the kind only the assignment history
    /// diagnoses.
    /// </summary>
    public bool ForgetStopOnReassign { get; set; }
}

/// <summary>Every live node, by id: the control queue a leader sends its start and stop commands over.</summary>
public sealed class ClusterNetwork
{
    private readonly ConcurrentDictionary<Guid, ClusterNode> _nodes = new();

    public NodeStorage Storage { get; } = new();

    /// <summary>The fixed agent family: Wolverine's <c>FakeAgentFamily</c> twelve.</summary>
    public static readonly Uri[] AllAgents =
        new[] { "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve" }
            .Select(x => new Uri($"fake://{x}"))
            .ToArray();

    internal void Join(ClusterNode node) => _nodes[node.Id] = node;

    internal void Leave(ClusterNode node) => _nodes.TryRemove(node.Id, out _);

    internal ClusterNode? Find(Guid id) => _nodes.GetValueOrDefault(id);

    public IReadOnlyList<ClusterNode> LiveNodes => _nodes.Values.OrderBy(x => x.Number).ToArray();
}

/// <summary>
/// One node: Wolverine's <c>IHost</c> + <c>WolverineRuntime</c>, reduced to heartbeat, leadership and
/// running agents.
/// </summary>
public sealed class ClusterNode : IAsyncDisposable
{
    private readonly ClusterNetwork _network;
    private readonly ClusterOptions _options;
    private readonly ConcurrentDictionary<Uri, byte> _running = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _loop = Task.CompletedTask;
    private volatile bool _isLeader;
    private int _evaluations;

    public ClusterNode(ClusterNetwork network, ClusterOptions options)
    {
        _network = network;
        _options = options;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public int Number { get; private set; }

    public bool IsLeader => _isLeader;

    public ClusterTracker Tracker { get; } = new();

    /// <summary>Wolverine's <c>host.RunningAgents()</c>.</summary>
    public IReadOnlyList<Uri> RunningAgents() => _running.Keys.OrderBy(x => x.ToString()).ToArray();

    public void Start()
    {
        Number = _network.Storage.Register(Id);
        _network.Join(this);
        Tracker.Publish(new NodeStarted(Number));

        // The first heartbeat is part of starting, so a node that starts alone has taken the leadership
        // and placed every agent before the next node can join. Without it the reproduction raced: a
        // second node joining inside the first 50ms was balanced onto directly, nothing was ever moved,
        // and the planted defect never showed.
        heartbeat();

        // Then the background loop, as Wolverine's health check and assignment loops are: everything it
        // publishes reaches observers on THAT thread, never the test's.
        _loop = Task.Run(loopAsync);
    }

    private async Task loopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.Period, _stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            heartbeat();
        }
    }

    private void heartbeat()
    {
        if (!_isLeader && _network.Storage.TryTakeLeadership(Id))
        {
            _isLeader = true;
            Tracker.Publish(new LeadershipAssumed(Number));
        }

        if (_isLeader) evaluate();
    }

    /// <summary>
    /// The leader's evaluation: spread the agents evenly over the live nodes, keeping each one where the
    /// table says it is when that node still has room, and send the starts and stops that takes.
    /// </summary>
    private void evaluate()
    {
        var nodes = _network.Storage.LoadAllNodes();
        if (nodes.Count == 0) return;

        var owner = nodes.SelectMany(n => n.ActiveAgents.Select(a => (Agent: a, n.NodeId)))
            .ToDictionary(x => x.Agent, x => x.NodeId);
        var numberOf = nodes.ToDictionary(x => x.NodeId, x => x.NodeNumber);

        var agents = ClusterNetwork.AllAgents;
        var target = new Dictionary<Guid, int>();
        for (var i = 0; i < nodes.Count; i++)
        {
            target[nodes[i].NodeId] = agents.Length / nodes.Count + (i < agents.Length % nodes.Count ? 1 : 0);
        }

        var placed = new Dictionary<Uri, Guid>();
        var load = nodes.ToDictionary(x => x.NodeId, _ => 0);

        foreach (var agent in agents)
        {
            if (owner.TryGetValue(agent, out var current) && load[current] < target[current])
            {
                placed[agent] = current;
                load[current]++;
            }
        }

        foreach (var agent in agents.Where(a => !placed.ContainsKey(a)))
        {
            var node = nodes.First(n => load[n.NodeId] < target[n.NodeId]).NodeId;
            placed[agent] = node;
            load[node]++;
        }

        var assignments = new List<AgentAssignment>();
        var commands = 0;

        foreach (var agent in agents)
        {
            int? original = owner.TryGetValue(agent, out var was) ? numberOf[was] : null;
            var assigned = placed[agent];
            assignments.Add(new AgentAssignment(agent, original, numberOf[assigned]));

            if (owner.TryGetValue(agent, out var from) && from == assigned) continue;

            if (owner.ContainsKey(agent) && !_options.ForgetStopOnReassign)
            {
                _network.Find(from)?.StopAgent(agent);
                commands++;
            }

            _network.Find(assigned)?.StartAgent(agent);
            commands++;
        }

        Tracker.Publish(new AgentAssignmentsChanged(Number, ++_evaluations,
            nodes.Select(x => x.NodeNumber).ToArray(), assignments, commands));
    }

    public void StartAgent(Uri agent)
    {
        _running[agent] = 0;
        _network.Storage.AddAssignment(Id, agent);
    }

    public void StopAgent(Uri agent)
    {
        _running.TryRemove(agent, out _);
        _network.Storage.RemoveAssignment(Id, agent);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _loop;

        _network.Leave(this);
        _network.Storage.Remove(Id);
        _running.Clear();
        _isLeader = false;

        Tracker.Publish(new NodeStopped(Number));
        _stopping.Dispose();
    }
}
