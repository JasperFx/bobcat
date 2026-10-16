using System.Text.Json;
using Bobcat.Ui.EventModel;
using Bobcat.Ui.Runs;
using Wolverine.Http;

namespace Bobcat.Ui.Agent;

/// <summary>
/// <see cref="AgentQueries"/> over HTTP, for the <c>bobcat</c> CLI's query commands (bobcat#524).
/// Every answer is JSON; a <c>{ "error": … }</c> answer is a 400, so a caller can tell a refusal
/// from a result without reading the body. Read-only, like the board.
/// </summary>
public static class AgentEndpoints
{
    [WolverineGet("/api/agent/runs")]
    public static IResult Runs(MonitorRunRegistry registry)
        => answer(AgentQueries.ListRuns(registry));

    [WolverineGet("/api/agent/run-status")]
    public static IResult RunStatus(MonitorRunRegistry registry, string? runId = null)
        => answer(AgentQueries.RunStatus(registry, runId));

    [WolverineGet("/api/agent/failing-tests")]
    public static IResult FailingTests(MonitorRunRegistry registry, string? runId = null)
        => answer(AgentQueries.FailingTests(registry, runId));

    [WolverineGet("/api/agent/failing-spec")]
    public static IResult FailingSpec(MonitorRunRegistry registry, EventModelStore store, string? runId = null, string? uid = null)
        => answer(AgentQueries.FailingSpec(registry, store, runId, uid));

    [WolverineGet("/api/agent/flaky-ledger")]
    public static IResult FlakyLedger(MonitorRunRegistry registry, string? runId = null)
        => answer(AgentQueries.FlakyLedger(registry, runId));

    [WolverineGet("/api/agent/event-model")]
    public static IResult EventModel(EventModelStore store, string? slice = null, string? domain = null)
        => answer(AgentQueries.EventModel(store, slice, domain));

    [WolverineGet("/api/agent/slice-coverage")]
    public static IResult SliceCoverage(EventModelStore store, MonitorRunRegistry registry)
        => answer(AgentQueries.SliceCoverage(store, registry));

    /// <summary><c>ctrf</c> (the default) or <c>junit</c>, for the most recent run unless one is named.</summary>
    [WolverineGet("/api/agent/export")]
    public static IResult Export(MonitorRunRegistry registry, string? runId = null, string? format = null)
    {
        var rendered = AgentQueries.ExportRun(registry, runId, format ?? "ctrf");
        if (IsError(rendered)) return Results.Content(rendered, "application/json", statusCode: StatusCodes.Status400BadRequest);

        return Results.Content(rendered, (format ?? "ctrf").Equals("junit", StringComparison.OrdinalIgnoreCase)
            ? "application/xml"
            : "application/json");
    }

    /// <summary>Block until a run settles (at most an hour); see <see cref="AgentQueries.AwaitRunCompletion"/>.</summary>
    [WolverineGet("/api/agent/await-run")]
    public static async Task<IResult> AwaitRun(MonitorRunRegistry registry, HttpContext context, string? runId = null, int timeoutSeconds = 600)
        => answer(await AgentQueries.AwaitRunCompletion(registry, runId, timeoutSeconds, context.RequestAborted));

    /// <summary>True when <paramref name="json"/> is an <c>{ "error": … }</c> object.</summary>
    public static bool IsError(string json)
    {
        if (!json.TrimStart().StartsWith('{')) return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out _)
                   && document.RootElement.EnumerateObject().Count() == 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IResult answer(string json)
        => Results.Content(json, "application/json",
            statusCode: IsError(json) ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK);
}
