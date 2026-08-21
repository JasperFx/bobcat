using Bobcat.Monitor;
using Bobcat.Monitor.Hosting;
using Bobcat.Monitor.Mcp;
using Bobcat.Monitor.Runs;
using JasperFx;
using Wolverine;
using Wolverine.Http;
using Wolverine.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    opts.UseSignalR();

    // Everything a publisher POSTs to /api/ingest is queued into the SignalRBatchAccumulator,
    // whose 100ms flush publishes a single BatchedWebSocketPayload envelope; this one rule
    // relays it to the browser. (BatchedWebSocketPayload is the only WebSocketMessage that
    // reaches the bus — individual MonitorEvents travel inside it.)
    opts.Publish(x =>
    {
        x.MessagesImplementing<WebSocketMessage>();
        x.ToSignalR();
    });
});

builder.Services.AddWolverineHttp();

// The monitor's memory + on-disk NDJSON archive. Singleton so ingestion, exports, and the
// UI's run list all see one registry; disposal closes the archive writers.
var retentionDays = builder.Configuration.GetValue<double?>("Monitor:RetentionDays");
builder.Services.AddSingleton(new MonitorRunRegistry(
    builder.Configuration["Monitor:DataPath"],
    retentionDays is { } days ? TimeSpan.FromDays(days) : null));

// One instance wears both hats: the ingestion endpoint's queue and the hosted 100ms flush.
builder.Services.AddSingleton<SignalRBatchAccumulator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SignalRBatchAccumulator>());
builder.Services.AddHostedService<ArchiveRetentionService>();

// MCP server (CritterWatch *.Mcp shape): streamable HTTP, stateless so every tool call is a
// self-contained request. This is the agent-facing surface — every dashboard query, plus
// await_run_completion for blocking on a suite instead of polling it.
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<MonitorTools>();

var app = builder.Build();

// No-op in a dev build (Vite serves the SPA); in an EmbedFrontend build this serves the
// embedded console at the root with an index.html fallback for the Vue Router's routes.
app.UseBobcatMonitorSpa();

app.MapWolverineEndpoints();
app.MapWolverineSignalRHub("/api/messages");
app.MapMcp("/api/mcp");

return await app.RunJasperFxCommands(args);

// Lets a test host bootstrap this exact application in-process: Alba / WebApplicationFactory
// resolve the entry point through this type. The Bobcat.Monitor.Specs end-to-end suite drives
// the viewer over TestServer this way: no port, no browser, the real endpoints.
public partial class Program;
