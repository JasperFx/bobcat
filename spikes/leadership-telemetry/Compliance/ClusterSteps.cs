using Bobcat;
using Cluster;
using Compliance.Telemetry;

namespace Compliance;

/// <summary>
/// The leadership suite's vocabulary. Wolverine's helpers are <c>private</c> methods and extension
/// methods on <c>IHost</c>; a step helper has to be callable from the generated interceptor, so these are
/// public on a non-generic class the base holds (BOBCAT039, #535).
/// </summary>
public sealed class ClusterSteps : IAsyncDisposable
{
    public static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(50);

    private readonly ClusterOptions _options;
    private readonly List<ClusterNode> _nodes = new();

    public ClusterSteps(ClusterOptions options)
    {
        _options = options;
    }

    public ClusterNetwork Network { get; } = new();

    public AgentAssignmentTelemetry Telemetry { get; } = new();

    /// <summary>How long a wait gives the cluster to settle. Wolverine's waits use 10–60 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    private ClusterNode node(int number) => _nodes.Single(x => x.Number == number);

    // Every step attaches the timeline, because xUnit v3 runs IAsyncLifetime.InitializeAsync -- where the
    // first node starts -- BEFORE the scenario's recording opens: SpecLog, SpecReport and SpecAttachment
    // are silent no-ops there. The node's events are not lost, because the telemetry buffers them.

    /// <summary>Wolverine's <c>startHostAsync()</c>, which also wires the event observer.</summary>
    [Given("a node starts")]
    public ClusterNode ANodeStarts()
    {
        Telemetry.AttachToScenario();

        var node = new ClusterNode(Network, _options);
        Telemetry.Watch(node);
        node.Start();
        _nodes.Add(node);

        SpecLog.Write($"node {node.Number} started as {node.Id}");
        return node;
    }

    [When("node {number} stops")]
    public async Task NodeStops(int number)
    {
        Telemetry.AttachToScenario();

        var stopping = node(number);
        var wasLeader = stopping.IsLeader;
        await stopping.DisposeAsync();
        _nodes.Remove(stopping);

        SpecLog.Write($"node {number} stopped{(wasLeader ? ", taking the leadership with it" : "")}");
    }

    [Then("node {number} is the leader")]
    public Task NodeLeads(int number)
    {
        Telemetry.AttachToScenario();
        return SpecAssert.EventuallyAsync("leader", () => _nodes.SingleOrDefault(x => x.IsLeader)?.Number, number,
            Timeout, PollingInterval);
    }

    [Then("some surviving node becomes the leader")]
    public Task ASurvivorLeads()
    {
        Telemetry.AttachToScenario();
        return SpecAssert.EventuallyAsync(() => _nodes.Any(x => x.IsLeader), Timeout, PollingInterval,
            because: "a surviving node holds the leadership");
    }

    /// <summary>
    /// Wolverine's <c>WaitUntilAssignmentsChangeTo(w => w.ExpectRunningAgents(host, n)…)</c>, as
    /// <c>"1=6, 2=6"</c>. Narrates every change it sees while it waits as a step log, and on timeout
    /// records one cell per node plus the placement report, instead of a string in a TimeoutException.
    /// </summary>
    [Then("the agents settle as {placement}")]
    public async Task AgentsSettleAs(string placement)
    {
        Telemetry.AttachToScenario();

        var expected = placement.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', StringSplitOptions.TrimEntries))
            .ToDictionary(x => int.Parse(x[0]), x => int.Parse(x[1]));

        Dictionary<int, int> counts() => expected.Keys.ToDictionary(n => n, n => node(n).RunningAgents().Count);

        if (await pollAsync(() => expected.All(x => counts()[x.Key] == x.Value),
                () => string.Join(", ", counts().Select(x => $"node {x.Key} runs {x.Value}"))))
        {
            return;
        }

        AgentPlacementReport.Capture(Network);

        var final = counts();
        foreach (var (number, count) in expected)
        {
            SpecAssert.Check($"node {number}", final[number], count);
        }
    }

    /// <summary>
    /// Wolverine's <c>expectExactlyOneCopyOfEachAsync</c> + <c>expectOneCopyWithARowNamingItsNodeAsync</c>:
    /// every agent runs on exactly one node and its assignment row names that node.
    /// </summary>
    [Then("every agent runs exactly once, with a row naming its node")]
    public async Task EveryAgentRunsOnce()
    {
        Telemetry.AttachToScenario();

        (Uri Agent, int[] Runners, int? Row)[] placement()
        {
            var rows = Network.Storage.LoadAllNodes()
                .SelectMany(n => n.ActiveAgents.Select(a => (Agent: a, n.NodeNumber)))
                .ToDictionary(x => x.Agent, x => x.NodeNumber);

            return ClusterNetwork.AllAgents
                .Select(a => (a, _nodes.Where(n => n.RunningAgents().Contains(a)).Select(n => n.Number).ToArray(),
                    rows.TryGetValue(a, out var r) ? (int?)r : null))
                .ToArray();
        }

        bool healthy((Uri Agent, int[] Runners, int? Row) x) => x.Runners.Length == 1 && x.Row == x.Runners[0];

        if (await pollAsync(() => placement().All(healthy),
                () => $"{placement().Count(healthy)} of {ClusterNetwork.AllAgents.Length} agents healthy"))
        {
            return;
        }

        AgentPlacementReport.Capture(Network);

        var wrong = placement().Where(x => !healthy(x)).ToArray();
        SpecAssert.Check("duplicated agents", wrong.Count(x => x.Runners.Length > 1), 0);
        SpecAssert.Check("agents running nowhere", wrong.Count(x => x.Runners.Length == 0), 0);
        SpecAssert.Check("rows naming the wrong node", wrong.Count(x => x.Runners.Length == 1), 0);
    }

    /// <summary>
    /// Poll until <paramref name="done"/>, logging <paramref name="describe"/> each time what it says
    /// changes -- a handful of lines per wait, not one per poll, which is what keeps the step under
    /// <see cref="Bobcat.Engine.StepLog.MaxLines"/>.
    /// </summary>
    private async Task<bool> pollAsync(Func<bool> done, Func<string> describe)
    {
        var started = Telemetry.Elapsed;
        string? last = null;

        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var now = describe();
            if (now != last)
            {
                SpecLog.Write($"{(Telemetry.Elapsed - started).TotalSeconds:0.00}s: {now}");
                last = now;
            }

            if (done()) return true;
            if (timeout.IsCancellationRequested) break;

            await Task.Delay(PollingInterval);
        }

        SpecLog.Write($"gave up after {Timeout.TotalSeconds:0.#}s");
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes.AsEnumerable().Reverse().ToArray())
        {
            await node.DisposeAsync();
        }
    }
}
