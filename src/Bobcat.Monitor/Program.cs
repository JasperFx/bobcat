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

    // Everything a publisher POSTs to /api/ingest cascades out of the endpoint as messages;
    // this one rule relays all of them to the browser. Per-message sends are fine at test-run
    // event rates — CritterWatch's 100ms SignalRBatchAccumulator is the pattern to lift if a
    // monster suite ever proves otherwise (the frontend already rAF-batches on its side).
    opts.Publish(x =>
    {
        x.MessagesImplementing<WebSocketMessage>();
        x.ToSignalR();
    });
});

builder.Services.AddWolverineHttp();

// The monitor's memory + on-disk NDJSON archive. Singleton so ingestion, exports, and the
// UI's run list all see one registry; disposal closes the archive writers.
builder.Services.AddSingleton(new MonitorRunRegistry(builder.Configuration["Monitor:DataPath"]));

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
