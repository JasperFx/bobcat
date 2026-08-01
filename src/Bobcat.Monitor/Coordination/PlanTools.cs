using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Bobcat.Monitor.Coordination;

/// <summary>
/// The agent-facing read side of the coordination context, same shape as
/// <see cref="Mcp.MonitorTools"/>. Read-only on purpose for now — the claim/report/await
/// tools arrive with the event-sourced status layer, and until then an agent can learn what
/// work is planned and what depends on what, but asserts nothing.
/// </summary>
[McpServerToolType]
public class PlanTools
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string toJson(object value) => JsonSerializer.Serialize(value, jsonOptions);

    [McpServerTool(Name = "list_plans")]
    [Description(
        "List every coordination plan this monitor knows about — cross-repo DAGs of GitHub " +
        "issues, NuGet publish/consume steps, and test-run gates. Invalid plan documents are " +
        "listed with their errors. Use the slug with get_plan.")]
    public static string ListPlans(PlanRegistry registry)
        => toJson(registry.All().Select(PlanViews.Summarize).ToArray());

    [McpServerTool(Name = "get_plan")]
    [Description(
        "One plan's full DAG: nodes (kind, repo, package, bump, merge policy), depends_on " +
        "edges, and dependencyOrder — every node listed after everything it depends on. " +
        "A node with an empty depends_on whose upstreams are all done is ready work.")]
    public static string GetPlan(
        PlanRegistry registry,
        [Description("Plan slug from list_plans.")] string slug)
    {
        var plan = registry.Find(slug);
        return plan is null
            ? toJson(new { error = $"no plan '{slug}' — list_plans shows what this monitor knows" })
            : toJson(PlanViews.Detail(plan));
    }
}
