using Bobcat.Monitor.Contracts;
using Bobcat.Monitor.Runs;
using Wolverine;
using Wolverine.Http;

namespace Bobcat.Monitor;

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
        => new("bobcat-monitor",
            typeof(IngestionEndpoint).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>
    /// The single ingestion seam: a batch of MonitorEvents, cascaded straight onto Wolverine's
    /// bus where the publish rule in Program.cs relays every WebSocketMessage to the SignalR
    /// hub. Accepted (202) is deliberate — ingestion is fire-and-forget for the publisher, and
    /// nothing downstream of the cascade can fail the POST.
    /// </summary>
    [WolverinePost("/api/ingest")]
    public static (IResult, OutgoingMessages) Ingest(IngestBatch batch, MonitorRunRegistry registry)
    {
        // Fold + archive synchronously (cheap local work), THEN cascade to SignalR. Ordering
        // matters: the registry must never lag behind what a browser has already been shown.
        registry.Record(batch.Events);

        var messages = new OutgoingMessages();
        messages.AddRange(batch.Events);
        return (Results.Accepted(), messages);
    }
}
