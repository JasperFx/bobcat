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

var app = builder.Build();

app.MapWolverineEndpoints();
app.MapWolverineSignalRHub("/api/messages");

return await app.RunJasperFxCommands(args);
