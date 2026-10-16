using Bobcat.Ui.EventModel;
using Bobcat.Ui.Hosting;
using Wolverine;
using Wolverine.Http;
using Wolverine.SignalR;

namespace Bobcat.Ui;

/// <summary>
/// Builds the <c>bobcat ui</c> web application (bobcat#524): the run board and the Event Model
/// canvas, the publisher wire (<c>/api/ingest</c>, <c>/api/runs</c>, <c>/api/event-model</c>, the
/// exports), the resident-runner wire (<c>/api/runners</c>), the SignalR hub the SPA listens on,
/// and the agent queries the <c>bobcat</c> CLI reads (<c>/api/agent/*</c>).
/// </summary>
/// <remarks>
/// One method builds the application and one maps it, so the <c>ui</c> command and the end-to-end
/// specs (<c>Bobcat.Ui.Specs</c>, over Alba's TestServer) compose exactly the same host.
/// </remarks>
public static class BobcatUiHost
{
    /// <summary>The address every Bobcat publisher already probes.</summary>
    public const int DefaultPort = 5525;

    /// <summary>
    /// Configure <paramref name="builder"/> as a Bobcat UI host. The caller chooses the URLs; the
    /// <c>ui</c> command binds <see cref="DefaultPort"/> unless told otherwise.
    /// </summary>
    /// <param name="idleShutdown">
    /// Arm the idle ceiling (issue #200). Off for <c>bobcat ui</c>, which a person started and
    /// stops with Ctrl+C; on for the console <c>watch-event-model</c> starts in the background,
    /// which nobody owns once its terminal is gone.
    /// </param>
    public static WebApplicationBuilder Configure(WebApplicationBuilder builder, bool idleShutdown)
    {
        builder.Host.UseWolverine(opts =>
        {
            opts.ServiceName = "Bobcat";

            // The entry assembly is the `bobcat` tool (or a test host), so the endpoints and the
            // runner-wire handlers in this assembly are found only because this line says so. A
            // missing line fails as 404s on a host that started cleanly.
            opts.ApplicationAssembly = typeof(BobcatUiHost).Assembly;

            opts.UseSignalR();

            // Everything a publisher POSTs to /api/ingest is queued into the SignalRBatchAccumulator,
            // whose 100ms flush publishes a single BatchedWebSocketPayload; this rule relays it, and
            // the Event Model change notice, to the browser.
            opts.Publish(x =>
            {
                x.MessagesImplementing<WebSocketMessage>();
                x.ToSignalR();
            });
        });

        builder.Services.AddWolverineHttp();
        builder.Services.AddBobcatUi(builder.Configuration, idleShutdown);

        return builder;
    }

    /// <summary>Map the pipeline onto a built application.</summary>
    public static WebApplication Map(WebApplication app)
    {
        app.UseBobcatUiActivity();

        // No-op in a dev build (Vite serves the SPA on its own port and proxies /api here). In an
        // EmbedFrontend build it serves the embedded SPA at the root, with an index.html fallback
        // for the Vue Router's history-mode routes.
        app.UseBobcatUiSpa();

        // A query value that is present but unparseable answers 400 rather than binding the default
        // (?olderThan=yesterday must not eject every run).
        app.MapWolverineEndpoints(opts => opts.RejectUnparseableQueryValues = true);
        app.MapWolverineSignalRHub("/api/messages");

        return app;
    }

    /// <summary>The URL <c>bobcat ui</c> binds for a port.</summary>
    public static string UrlFor(int port) => $"http://localhost:{port}";

    /// <summary>The page a browser opens on: the run board.</summary>
    public static string HomePageFor(string url) => url.TrimEnd('/') + "/";

    /// <summary>The Event Model canvas.</summary>
    public static string EventModelPageFor(string url) => new EventModelWatchPlan(url, false, []).EventModelPageUrl;
}
