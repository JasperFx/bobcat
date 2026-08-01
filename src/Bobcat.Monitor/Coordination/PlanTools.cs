using System.ComponentModel;
using System.Text.Json;
using Bobcat.Monitor.Coordination.GitHub;
using Bobcat.Monitor.Coordination.NuGet;
using ModelContextProtocol.Server;

namespace Bobcat.Monitor.Coordination;

/// <summary>
/// The agent-facing surface of the coordination context, same shape as
/// <see cref="Mcp.MonitorTools"/>. Reads (list/get/status/ready) derive from observation;
/// claims are leased assertions (see <see cref="ClaimStore"/>); the await tools are the
/// token-optimization mechanism — an agent blocks here instead of burning tokens polling
/// GitHub and feeds itself. The awaits poll in-process for now, same as
/// await_run_completion; they become Wolverine-subscription-backed when the SQLite event
/// store lands, with no change to the tool surface.
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

    [McpServerTool(Name = "plan_status")]
    [Description(
        "The live view of one plan's DAG: every node's derived status (unrealized, unknown, " +
        "missing, open, claimed, pr-open, abandoned, done) from GitHub observation, plus the " +
        "ready list — nodes whose dependencies are all done. Ready unrealized work means " +
        "'materialize the issue and start'.")]
    public static string PlanStatusFor(
        PlanRegistry registry,
        ObservationStores stores,
        [Description("Plan slug from list_plans.")] string slug)
    {
        var plan = registry.Find(slug);
        if (plan is null) return toJson(new { error = $"no plan '{slug}' — list_plans shows what this monitor knows" });
        if (!plan.IsValid) return toJson(new { error = $"plan '{slug}' has document errors", errors = plan.Errors });

        return toJson(PlanStatus.For(plan, stores));
    }

    [McpServerTool(Name = "next_ready_nodes")]
    [Description(
        "Ready work across every valid plan (or one plan): nodes whose dependencies are all " +
        "done and which are not themselves done, missing, or running. Each entry carries " +
        "claimedBy when another agent already holds it — pick unclaimed work, then claim_node.")]
    public static string NextReadyNodes(
        PlanRegistry registry,
        ObservationStores stores,
        [Description("Optional plan slug to scope to; omit for all plans.")] string? plan = null)
    {
        var plans = registry.All()
            .Where(x => x.IsValid && (plan is null || x.Slug == plan))
            .ToArray();

        if (plan is not null && plans.Length == 0)
            return error($"no valid plan '{plan}' — list_plans shows what this monitor knows");

        var ready = plans
            .Select(x => PlanStatus.For(x, stores))
            .SelectMany(status => status.Nodes
                .Where(n => n.Ready)
                .Select(n => new { plan = status.Slug, node = n }))
            .ToArray();

        return toJson(ready);
    }

    [McpServerTool(Name = "claim_node")]
    [Description(
        "Claim a plan node for in-flight work, or renew your own claim. Refused with the " +
        "holder's name when someone else has it, and refused outright for done nodes. The " +
        "claim is a LEASE (default 30 minutes) — report_node renews it; a crashed agent's " +
        "claim simply expires. Claiming does not touch GitHub.")]
    public static string ClaimNode(
        PlanRegistry registry,
        ObservationStores stores,
        [Description("Plan slug from list_plans.")] string plan,
        [Description("Node id within the plan.")] string node,
        [Description("Your agent identity — shown on the dashboard and to other agents.")] string agent,
        [Description("Lease minutes before the claim expires unrenewed (default 30, max 240).")]
        double leaseMinutes = 30)
    {
        var (status, problem) = statusAndNode(registry, stores, plan, node);
        if (problem is not null) return problem;

        if (status!.Status == "done") return error($"node '{node}' is already done — nothing to claim");

        var result = stores.Claims.TryClaim(plan, node, agent, TimeSpan.FromMinutes(leaseMinutes));
        if (!result.Succeeded)
        {
            return toJson(new
            {
                claimed = false,
                heldBy = result.Conflict!.Agent,
                heldUntil = result.Conflict.ExpiresAt,
                hint = "pick other work from next_ready_nodes, or wait for the lease to expire"
            });
        }

        return toJson(new
        {
            claimed = true,
            expiresAt = result.Claim!.ExpiresAt,
            node = status with { ClaimedBy = agent },
            readyWarning = status.Ready
                ? null
                : $"this node is not ready (status {status.Status}) — its dependencies may not be done"
        });
    }

    [McpServerTool(Name = "report_node")]
    [Description(
        "Report progress on a node you claimed: attaches/replaces your note (shown on the " +
        "dashboard) and renews the lease. Use for decisions worth surfacing — 'reproduced, " +
        "fix is in the parser', not a transcript.")]
    public static string ReportNode(
        PlanRegistry registry,
        ObservationStores stores,
        [Description("Plan slug.")] string plan,
        [Description("Node id.")] string node,
        [Description("Your agent identity — must match the claim.")] string agent,
        [Description("The note to attach (replaces the previous one).")] string note,
        [Description("Lease minutes to renew for (default 30, max 240).")] double leaseMinutes = 30)
    {
        var renewed = stores.Claims.Report(plan, node, agent, note, TimeSpan.FromMinutes(leaseMinutes));
        return renewed is null
            ? error($"no live claim on '{plan}/{node}' held by '{agent}' — claim_node first")
            : toJson(new { reported = true, expiresAt = renewed.ExpiresAt });
    }

    [McpServerTool(Name = "release_node")]
    [Description(
        "Release your claim on a node — done working or handing it back. Status stays " +
        "whatever observation says it is: finishing the work is proven by the issue closing, " +
        "the version appearing, the suite passing — never by this call.")]
    public static string ReleaseNode(
        PlanRegistry registry,
        ObservationStores stores,
        [Description("Plan slug.")] string plan,
        [Description("Node id.")] string node,
        [Description("Your agent identity — must match the claim.")] string agent)
    {
        return stores.Claims.Release(plan, node, agent)
            ? toJson(new { released = true })
            : error($"no live claim on '{plan}/{node}' held by '{agent}'");
    }

    [McpServerTool(Name = "await_dependencies")]
    [Description(
        "Block until every dependency of a node is done — the token-efficient alternative to " +
        "polling plan_status in a loop. Returns 'ready' with the node's current view, or " +
        "'timeout' with the dependencies still blocking.")]
    public static async Task<string> AwaitDependencies(
        PlanRegistry registry,
        ObservationStores stores,
        [Description("Plan slug.")] string plan,
        [Description("Node id whose dependencies to wait for.")] string node,
        [Description("Seconds to wait before giving up (default 600, max 3600).")] int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 3600));

        while (true)
        {
            var (status, problem) = statusAndNode(registry, stores, plan, node);
            if (problem is not null) return problem;

            var registered = registry.Find(plan)!;
            var full = PlanStatus.For(registered, stores);
            var doneIds = full.Nodes.Where(n => n.Status == "done").Select(n => n.Id).ToHashSet();
            var blocking = status!.DependsOn.Where(d => !doneIds.Contains(d)).ToArray();

            if (blocking.Length == 0)
            {
                return toJson(new { outcome = "ready", node = status });
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return toJson(new
                {
                    outcome = "timeout",
                    waitedSeconds = timeoutSeconds,
                    blockedOn = full.Nodes.Where(n => blocking.Contains(n.Id)).ToArray()
                });
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    [McpServerTool(Name = "await_package_version")]
    [Description(
        "Block until a feed serves a package version >= minVersion — how a downstream agent " +
        "waits out an upstream publish without polling the feed itself. Queries the feed " +
        "directly, so it works for any package, not just ones a plan references.")]
    public static async Task<string> AwaitPackageVersion(
        NuGetFeeds feeds,
        ObservationStores stores,
        [Description("Feed name from monitor config; 'nuget.org' is built in.")] string feed,
        [Description("The package id.")] string package,
        [Description("Resolve once any version >= this is observed.")] string minVersion,
        [Description("Seconds to wait before giving up (default 600, max 3600).")] int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        var target = PackageVersion.TryParse(minVersion);
        if (target is null) return error($"'{minVersion}' is not a parseable package version");

        var resolved = feeds.Resolve(feed);
        if (resolved is null) return error($"feed '{feed}' is not configured (Monitor:Feeds:{feed})");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 3600));
        string[] lastVersions = [];

        while (true)
        {
            try
            {
                lastVersions = (await resolved.GetVersionsAsync(package, cancellationToken)).ToArray();

                // The await IS an observation — fold it so the dashboard benefits too.
                stores.NuGet.Upsert(new NuGetObservation(
                    feed, package, lastVersions, null, DateTimeOffset.UtcNow));

                var satisfying = lastVersions
                    .Select(PackageVersion.TryParse)
                    .Where(x => x is not null && x.CompareTo(target) >= 0)
                    .Select(x => x!)
                    .ToArray();

                if (satisfying.Length > 0)
                {
                    return toJson(new { outcome = "observed", version = satisfying.Max()!.Text, feed, package });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A transient feed failure inside an await is just a tick with no news.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return toJson(new
                {
                    outcome = "timeout",
                    waitedSeconds = timeoutSeconds,
                    latest = lastVersions
                        .Select(PackageVersion.TryParse)
                        .Where(x => x is not null)
                        .Max()?.Text
                });
            }

            // Never sleep past the deadline — a 1s timeout should time out in ~1s, not
            // after a full feed-poll tick.
            var remaining = deadline - DateTimeOffset.UtcNow;
            var tick = remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
            await Task.Delay(tick < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : tick, cancellationToken);
        }
    }

    /// <summary>Common guard: valid plan, known node — returns the node's current status view.</summary>
    private static (NodeStatusView? Node, string? Problem) statusAndNode(
        PlanRegistry registry, ObservationStores stores, string plan, string node)
    {
        var registered = registry.Find(plan);
        if (registered is null)
            return (null, error($"no plan '{plan}' — list_plans shows what this monitor knows"));
        if (!registered.IsValid)
            return (null, toJson(new { error = $"plan '{plan}' has document errors", errors = registered.Errors }));

        var status = PlanStatus.For(registered, stores);
        var view = status.Nodes.FirstOrDefault(x => x.Id == node);
        return view is null
            ? (null, error($"plan '{plan}' has no node '{node}'"))
            : (view, null);
    }

    private static string error(string message) => toJson(new { error = message });

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
