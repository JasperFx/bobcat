using Bobcat.Console;
using Bobcat.Console.EventModel;
using Bobcat.Console.Hosting;
using Bobcat.Console.Mcp;
using Bobcat.Console.Runs;
using JasperFx;
using Wolverine;
using Wolverine.Http;
using Wolverine.SignalR;

var builder = WebApplication.CreateBuilder(args);

// The console's port is 5525 everywhere a CLIENT looks — MonitorPublisher.DefaultUrl, the
// event-model watch plan, the Vite dev proxy, the docs. The SERVER only ever had it in
// launchSettings.json, which is a `dotnet run` file the packaged tool never sees, so `bobcat run`
// fell to Kestrel's bare :5000 (issue #200): unreachable by every publisher, which all probe
// 5525, and squatting on the port every other ASP.NET default host on the box wants.
// ConsoleUrlAgreementTests pins this constant to MonitorPublisher.DefaultUrl, because the
// layering rule keeps the console from referencing Bobcat to share the literal outright.
//
// A default, not an override: ASPNETCORE_URLS still wins. It has to, because the command line
// cannot reach this — RunJasperFxCommands wraps an already-built WebApplication in a
// PreBuiltHostBuilder, and NetCoreInput.ApplyHostBuilderInput returns early for one, so
// `--config:urls=...` is silently inert.
if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls(EventModelWatchPlan.DefaultConsoleUrl);
}

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
// Two retention knobs, deliberately separate: RetentionDays bounds the ARCHIVE by age (and is
// the only setting that ever deletes anything), RetentionRuns bounds the BOARD by count and
// only ever ejects. See MonitorRunRegistry for why the count is per suite rather than per box.
var retentionDays = builder.Configuration.GetValue<double?>("Monitor:RetentionDays");
builder.Services.AddSingleton(new MonitorRunRegistry(
    builder.Configuration["Monitor:DataPath"],
    retentionDays is { } days ? TimeSpan.FromDays(days) : null,
    builder.Configuration.GetValue<int?>("Monitor:RetentionRuns")));

// The current Event Model descriptor (issue #108) — one document beside the run archives,
// pushed over PUT /api/event-model, rendered by the SPA's Event Model page.
builder.Services.AddSingleton(sp => new EventModelStore(sp.GetRequiredService<MonitorRunRegistry>().DataPath));

// One instance wears both hats: the ingestion endpoint's queue and the hosted 100ms flush.
builder.Services.AddSingleton<SignalRBatchAccumulator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SignalRBatchAccumulator>());
builder.Services.AddHostedService<ArchiveRetentionService>();

// Liveness, and the ceiling that acts on it (issue #200): a console with nothing connected and
// nothing publishing to it stops itself rather than outliving the session that started it.
builder.Services.AddSingleton<ConsoleActivity>();
builder.Services.AddHostedService<IdleShutdownService>();

// MCP server (CritterWatch *.Mcp shape): streamable HTTP, stateless so every tool call is a
// self-contained request. This is the agent-facing surface — every dashboard query, plus
// await_run_completion for blocking on a suite instead of polling it.
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<MonitorTools>();

var app = builder.Build();

// First in the pipeline, so every request counts — the SPA, /api/ingest, the SignalR hub, the
// MCP surface. A WebSocket is one request that never completes, which is exactly how an open
// dashboard tab keeps the idle ceiling below from ever starting.
var activity = app.Services.GetRequiredService<ConsoleActivity>();
app.Use(async (context, next) =>
{
    activity.Began();
    try
    {
        await next(context);
    }
    finally
    {
        activity.Ended();
    }
});

// No-op in a dev build (Vite serves the SPA); in an EmbedFrontend build this serves the
// embedded console at the root with an index.html fallback for the Vue Router's routes.
app.UseBobcatConsoleSpa();

app.MapWolverineEndpoints();
app.MapWolverineSignalRHub("/api/messages");
app.MapMcp("/api/mcp");

return await app.RunJasperFxCommands(args);

// Lets a test host bootstrap this exact application in-process: Alba / WebApplicationFactory
// resolve the entry point through this type. The Bobcat.Console.Specs end-to-end suite drives
// the viewer over TestServer this way: no port, no browser, the real endpoints.
public partial class Program;
