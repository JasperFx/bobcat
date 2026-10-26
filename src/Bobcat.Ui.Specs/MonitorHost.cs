using Alba;
using Bobcat.Ui.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bobcat.Ui.Specs;

/// <summary>
/// The viewer under test: the real <see cref="BobcatUiHost"/> composition, booted in-process over Alba's
/// TestServer, with its archive directory pointed at a throwaway folder so a spec run never
/// touches <c>~/.bobcat/monitor/runs</c>. One host for the whole suite, as an xUnit assembly
/// fixture; the registry is emptied before every test, because the board is persistent state and a
/// test that asserts about the WHOLE board (the bulk ejects, issue #197) would otherwise be reading
/// every run every earlier test left behind.
/// </summary>
/// <remarks>
/// This was a Bobcat resource while the suite was Gherkin on Bobcat.Mtp, and it could
/// <see cref="Restart"/> the viewer mid-scenario only because Bobcat 0.27.0 named that verb
/// (the hydration rule in docs/monitor-design.md is "a monitor restart forgets nothing"). Bobcat
/// 0.50.0 deletes the resource model with the Gherkin lane; an xUnit fixture can restart whatever
/// it owns, so the verb is simply a method now.
/// </remarks>
public sealed class MonitorHost : IAsyncLifetime
{
    private IAlbaHost? _host;

    public MonitorHost()
    {
        DataPath = Path.Combine(Path.GetTempPath(), "bobcat-monitor-specs", Guid.NewGuid().ToString("N"));
    }

    /// <summary>Where this viewer instance archives runs — a fresh temp folder per suite run.</summary>
    public string DataPath { get; }

    public string EjectedPath => Path.Combine(DataPath, MonitorRunRegistry.EjectedFolder);

    public string ArchiveFileFor(Guid runId) => Path.Combine(DataPath, $"{runId}.ndjson");

    public string EjectedFileFor(Guid runId) => Path.Combine(EjectedPath, $"{runId}.ndjson");

    public IAlbaHost AlbaHost => _host
        ?? throw new InvalidOperationException("The viewer has not been started.");

    public IHost Host => AlbaHost;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(DataPath);
        _host = await boot();
    }

    /// <summary>
    /// Throw the running viewer away and boot a fresh one over the SAME archive directory — the
    /// monitor-restart case. Whatever the new instance knows, it learned by replaying archives.
    /// </summary>
    public async Task Restart()
    {
        // Nothing in this suite resolves services across a restart: every step goes over HTTP.
        if (_host != null) await _host.DisposeAsync();
        _host = await boot();
    }

    private Task<IAlbaHost> boot()
    {
        // The same composition `bobcat ui` runs (BobcatUiHost), over Alba's TestServer.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // The viewer has no content to serve in a dev build (no appsettings, the SPA is only
            // embedded when EmbedFrontend=true), so any existing directory will do.
            ContentRootPath = AppContext.BaseDirectory
        });

        // The test platform owns stdout; ASP.NET's per-request info logging would drown the run
        // summary in request traces. Warnings and up still reach the console.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration["Monitor:DataPath"] = DataPath;
        // Retention is its own concern (ArchiveRetentionTests, RunRetentionTests) — both policies
        // are inert here so no scenario can depend on the clock, or find its own runs quietly
        // evicted once a suite name has been used more than ten times.
        builder.Configuration["Monitor:RetentionDays"] = "0";
        builder.Configuration["Monitor:RetentionRuns"] = "0";
        // Live orphan detection (bobcat#784) is on the clock too; OrphanDetectionTests drives it on a
        // fake one. Here a run stays live until a restart orphans it, as every spec expects.
        builder.Configuration["Monitor:OrphanSeconds"] = "0";

        BobcatUiHost.Configure(builder, idleShutdown: false);

        return global::Alba.AlbaHost.For(builder, app => BobcatUiHost.Map(app));
    }

    /// <summary>
    /// Empty the board. Unconditional, unlike a bulk eject: this is the suite clearing its own
    /// state between tests, not a user asking, so a run left unfinished by an earlier test goes
    /// too. The archives move to <c>ejected/</c> as ever and the folder is thrown
    /// away with the resource.
    /// </summary>
    public Task ResetAsync()
    {
        if (_host == null) return Task.CompletedTask;

        var registry = _host.Services.GetRequiredService<MonitorRunRegistry>();
        foreach (var run in registry.All().ToList()) registry.Remove(run.RunId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Forget every published Event Model half: delete the store's files and boot a fresh viewer over the
    /// same folder, since the store reads them only at startup. The Gherkin feature leaned on its scenarios
    /// running in file order ("nothing published yet" went first); xUnit promises no order, so the Event
    /// Model specs start from this instead.
    /// </summary>
    public async Task ForgetEventModelAsync()
    {
        foreach (var file in Directory.EnumerateFiles(DataPath, "event-model*.json")) File.Delete(file);
        await Restart();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.DisposeAsync();
        _host = null;

        try
        {
            Directory.Delete(DataPath, recursive: true);
        }
        catch
        {
            // A temp folder that would not delete is not worth failing the run over.
        }
    }
}
