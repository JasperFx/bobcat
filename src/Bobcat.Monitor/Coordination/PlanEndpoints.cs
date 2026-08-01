using Bobcat.Monitor.Coordination.GitHub;
using Bobcat.Monitor.Coordination.NuGet;
using Wolverine.Http;

namespace Bobcat.Monitor.Coordination;

public static class PlanEndpoints
{
    [WolverineGet("/api/plans")]
    public static PlanSummary[] All(PlanRegistry registry)
        => registry.All().Select(PlanViews.Summarize).ToArray();

    /// <summary>The live DAG: per-node derived status plus the ready set. 409 for a plan
    /// that registered with errors — there is no DAG to report on.</summary>
    [WolverineGet("/api/plans/{slug}/status")]
    public static IResult Status(string slug, PlanRegistry registry, ObservationStores stores)
    {
        var plan = registry.Find(slug);
        if (plan is null) return Results.NotFound();

        return plan.IsValid
            ? Results.Ok(PlanStatus.For(plan, stores))
            : Results.Conflict(new { errors = plan.Errors });
    }

    [WolverineGet("/api/plans/{slug}")]
    public static IResult Find(string slug, PlanRegistry registry)
    {
        var plan = registry.Find(slug);
        return plan is null ? Results.NotFound() : Results.Ok(PlanViews.Detail(plan));
    }

    /// <summary>
    /// Push a plan document (raw YAML body). Unlike a broken file — which registers with its
    /// errors so the dashboard can show it — an invalid push is refused with a 400: the
    /// pusher is right there to hear about it, and nothing half-parsed gets registered.
    /// </summary>
    [WolverinePut("/api/plans/{slug}")]
    public static async Task<IResult> Put(string slug, HttpRequest request, [NotBody] PlanRegistry registry)
    {
        using var reader = new StreamReader(request.Body);
        var yaml = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);

        var result = registry.Push(slug, yaml);
        if (result.Succeeded) return Results.Ok(PlanViews.Summarize(result.Plan!));

        return result.FileOwned
            ? Results.Conflict(new { errors = result.Errors })
            : Results.BadRequest(new { errors = result.Errors });
    }

    /// <summary>Removes a pushed plan. File-backed plans 409 — a rescan would just resurrect
    /// them, so the file is the thing to delete.</summary>
    [WolverineDelete("/api/plans/{slug}")]
    public static IResult Remove(string slug, [NotBody] PlanRegistry registry)
        => registry.Remove(slug) switch
        {
            RemovePlanResult.Removed => Results.NoContent(),
            RemovePlanResult.FileOwned => Results.Conflict(
                new { errors = new[] { $"plan '{slug}' is file-backed — delete the file and rescan" } }),
            _ => Results.NotFound()
        };

    /// <summary>Resync with the plans directory — the "I just edited the file" button.</summary>
    [WolverinePost("/api/plans/rescan")]
    public static RescanResult Rescan([NotBody] PlanRegistry registry) => registry.Rescan();
}
