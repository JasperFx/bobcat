using Bobcat.Engine;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// The <c>docs/marker-steps.md</c> gather-then-stop example (bobcat#669): a wait step that gives up
/// reports every disagreement it saw as a wrong, then throws <see cref="SpecCriticalException"/>
/// because the rest of the scenario depends on it.
/// </summary>
public class ClusterSteps
{
    private readonly Dictionary<string, List<string>> _assignments = new();
    private readonly List<string> _agents = new();

    [Given("agents {agents} are assigned")]
    internal void AgentsAreAssigned(string agents) => _agents.AddRange(agents.Split(',', StringSplitOptions.TrimEntries));

    [When("node {node} runs {agents}")]
    internal void NodeRuns(string node, string agents)
    {
        foreach (var agent in agents.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!_assignments.TryGetValue(agent, out var nodes)) _assignments[agent] = nodes = new();
            nodes.Add(node);
        }
    }

    [Then("the agents settle on one node each")]
    internal void TheAgentsSettle()
    {
        var duplicated = describe(_agents.Where(a => _assignments.GetValueOrDefault(a)?.Count > 1));
        var missing = describe(_agents.Where(a => !_assignments.ContainsKey(a)));

        if (duplicated == "none" && missing == "none") return;

        // #region gather-then-stop
        SpecAssert.Check("agents running on more than one node", duplicated, "none");   // fails
        SpecAssert.Check("agents not running anywhere", missing, "none");              // passes
        throw new SpecCriticalException("the agents did not settle; the rest of the scenario depends on it");
        // #endregion gather-then-stop
    }

    private static string describe(IEnumerable<string> agents)
    {
        var list = agents.ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }
}
