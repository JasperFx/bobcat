using Bobcat.Ui.Contracts;
using Bobcat.Ui.Runs;
using Wolverine.Http;

namespace Bobcat.Ui;

public record IngestBatch(MonitorEvent[] Events);

public record PingResponse(string Tool, string Version);

public static class IngestionEndpoint
{
    /// <summary>
    /// The publisher's liveness probe. A test run probes this once at startup (tight timeout)
    /// and disables its publisher for the whole run when nothing answers — the monitor being
    /// down must cost a run exactly one failed HTTP call.
    /// </summary>
    [WolverineGet("/api/ping")]
    public static PingResponse Ping()
        => new("bobcat",
            typeof(IngestionEndpoint).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>
    /// The single ingestion seam: a batch of MonitorEvents, folded into the registry and
    /// queued for the SignalR batch flush. Accepted (202) is deliberate — ingestion is
    /// fire-and-forget for the publisher, and nothing downstream of the queue can fail the POST.
    /// </summary>
    [WolverinePost("/api/ingest")]
    public static IResult Ingest(IngestBatch batch, MonitorRunRegistry registry, SignalRBatchAccumulator accumulator)
    {
        // A body with no "events" binds as a null array, and a null entry in it as a null event.
        // Either used to reach the registry and answer 500 — found by walking every route with an
        // empty body (CoordinationLicenseSpecs). A batch that says nothing is the sender's error.
        if (batch.Events is null) return Results.BadRequest(new { errors = new[] { "a batch carries its events in \"events\"" } });
        var events = batch.Events.Where(e => e is not null).ToArray();

        // Fold + archive synchronously (cheap local work), THEN queue for SignalR. Ordering
        // matters: the registry must never lag behind what a browser has already been shown.
        registry.Record(events);
        accumulator.Enqueue(events);
        return Results.Accepted();
    }
}
