using Bobcat.Ui.EventModel;
using Bobcat.Ui.Hosting;
using Bobcat.Ui.Runners;
using Bobcat.Ui.Runs;

namespace Bobcat.Ui;

/// <summary>
/// Everything the run console needs registered, in one place. <see cref="BobcatUiHost"/> is the
/// one caller; it is kept apart from the host so the registrations read as a list.
/// </summary>
/// <remarks>
/// ⚠️ <see cref="IdleShutdownService"/> is registered only when asked for. See <see cref="AddBobcatUi"/>.
/// </remarks>
public static class ConsoleComposition
{
    /// <summary>
    /// Registers the run registry, the Event Model store, the SignalR batch accumulator and the
    /// activity counter. Idempotent registration is NOT attempted — call it once.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Read for the <c>Monitor:*</c> keys.</param>
    /// <param name="idleShutdown">
    /// Whether to arm the idle ceiling. The ceiling exists because a detached console was found
    /// alive 20h52m after its session ended, wedged on a port the next repository's gate wanted
    /// (issue #200). <c>bobcat ui</c> leaves it off — a person started it and stops it — and the
    /// console <c>watch-event-model</c> starts in the background turns it on.
    /// </param>
    public static IServiceCollection AddBobcatUi(
        this IServiceCollection services, IConfiguration configuration, bool idleShutdown)
    {
        // The console's memory + on-disk NDJSON archive. Singleton so ingestion, exports, and the
        // UI's run list all see one registry; disposal closes the archive writers.
        // Two retention knobs, deliberately separate: RetentionDays bounds the ARCHIVE by age (and
        // is the only setting that ever deletes anything), RetentionRuns bounds the BOARD by count
        // and only ever ejects. See MonitorRunRegistry for why the count is per suite.
        var retentionDays = configuration.GetValue<double?>("Monitor:RetentionDays");

        // The one place a local path becomes a canonical owner/name (stoat#64) — shared by the run
        // registry and the session source, so a run and an agent in the same checkout agree.
        var identity = new Repositories.GitRepositoryIdentity();
        services.AddSingleton<Repositories.IRepositoryIdentity>(identity);

        services.AddSingleton(new MonitorRunRegistry(
            configuration["Monitor:DataPath"],
            retentionDays is { } days ? TimeSpan.FromDays(days) : null,
            configuration.GetValue<int?>("Monitor:RetentionRuns"),
            identity));

        // The current Event Model descriptor (issue #108) — one document per source beside the run
        // archives, pushed over PUT /api/event-model/{source}, merged on GET.
        services.AddSingleton(sp =>
            new EventModelStore(sp.GetRequiredService<MonitorRunRegistry>().DataPath));

        // One instance wears both hats: the ingestion endpoint's queue and the hosted 100ms flush.
        services.AddSingleton<SignalRBatchAccumulator>();
        services.AddHostedService(sp => sp.GetRequiredService<SignalRBatchAccumulator>());
        services.AddHostedService<ArchiveRetentionService>();

        // The resident runners and their command queues (stoat#77, back from Stoat with the board):
        // in memory, because a runner re-registers on every start.
        services.AddSingleton(_ => new RunnerRegistry());

        services.AddSingleton<ConsoleActivity>();
        if (idleShutdown) services.AddHostedService<IdleShutdownService>();

        return services;
    }

    /// <summary>
    /// Counts every in-flight request for the activity signal. First in the pipeline, so the SPA,
    /// <c>/api/ingest</c>, the SignalR hub and the MCP surface all count. A WebSocket is one
    /// request that never completes, which is how an open dashboard keeps the ceiling from
    /// starting. With the ceiling disarmed nothing reads the counter, and the middleware is two
    /// interlocked increments per request — kept rather than made conditional so the standalone
    /// tool and the folded host run the identical pipeline.
    /// </summary>
    public static WebApplication UseBobcatUiActivity(this WebApplication app)
    {
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

        return app;
    }
}
