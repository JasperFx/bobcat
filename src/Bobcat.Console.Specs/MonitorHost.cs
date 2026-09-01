using Alba;
using Bobcat.Alba;
using Bobcat.Console.Runs;
using Bobcat.Runtime;
using JasperFx.CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bobcat.Console.Specs;

/// <summary>
/// The viewer under test: Bobcat.Console's real <c>Program</c>, booted in-process over Alba's
/// TestServer, with its archive directory pointed at a throwaway folder so a spec run never
/// touches <c>~/.bobcat/monitor/runs</c>. One resource for the whole suite; the registry is
/// emptied between scenarios, because the board is persistent state and a scenario that asserts
/// about the WHOLE board (the bulk ejects, issue #197) would otherwise be reading every run
/// every earlier scenario left behind.
/// </summary>
/// <remarks>
/// Not an <see cref="AlbaResource{TProgram}"/>, though it is the same shape, because the
/// ejection feature needs to <see cref="Restart"/> the viewer mid-scenario (the hydration rule in
/// docs/monitor-design.md is "a monitor restart forgets nothing") and <c>AlbaResource</c> has no
/// verb for that — only start, reset, and dispose. That gap is part of the #86 finding.
/// </remarks>
public sealed class MonitorHost : IHostResource, IAlbaResource
{
    public const string ResourceName = "Monitor";

    private readonly ScenarioScope _scope;
    private IAlbaHost? _host;

    public MonitorHost()
    {
        DataPath = Path.Combine(Path.GetTempPath(), "bobcat-monitor-specs", Guid.NewGuid().ToString("N"));
        _scope = new ScenarioScope(Name, () => _host?.Services);
    }

    public string Name => ResourceName;

    /// <summary>Where this viewer instance archives runs — a fresh temp folder per suite run.</summary>
    public string DataPath { get; }

    public string EjectedPath => Path.Combine(DataPath, MonitorRunRegistry.EjectedFolder);

    public string ArchiveFileFor(Guid runId) => Path.Combine(DataPath, $"{runId}.ndjson");

    public string EjectedFileFor(Guid runId) => Path.Combine(EjectedPath, $"{runId}.ndjson");

    public IAlbaHost AlbaHost => _host
        ?? throw new InvalidOperationException($"Resource '{Name}' has not been started.");

    public IHost Host => AlbaHost;
    public IServiceProvider RootServices => _scope.Root;
    public IServiceProvider CurrentServices => _scope.Current;

    public async Task Start()
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
        // The scenario scope belongs to the old container and ends with it. Nothing in this suite
        // resolves scoped services across a restart — every step goes over HTTP.
        await _scope.End();
        if (_host != null) await _host.DisposeAsync();
        _host = await boot();
    }

    private Task<IAlbaHost> boot()
    {
        // Program.cs ends in RunJasperFxCommands; under WebApplicationFactory the command runner
        // has to start the already-built host rather than parse a command line. Same flag Alba's
        // own tests set for a JasperFx-hosted app.
        JasperFxEnvironment.AutoStartHost = true;

        return global::Alba.AlbaHost.For<Program>(builder =>
        {
            // WebApplicationFactory's fallback is "solution directory + assembly name", which is
            // wrong for anything under src/ — it looked for <repo>/Bobcat.Console/. The viewer has
            // no content to serve in a dev build (no appsettings, the SPA is only embedded when
            // EmbedFrontend=true), so any existing directory will do.
            builder.UseContentRoot(AppContext.BaseDirectory);
            // The MTP platform owns stdout; ASP.NET's per-request info logging would drown the
            // run summary in request traces. Warnings and up still reach the console.
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.UseSetting("Monitor:DataPath", DataPath);
            // Retention is its own concern (ArchiveRetentionTests, RunRetentionTests) — both
            // policies are inert here so no scenario can depend on the clock, or find its own
            // runs quietly evicted once a suite name has been used more than ten times.
            builder.UseSetting("Monitor:RetentionDays", "0");
            builder.UseSetting("Monitor:RetentionRuns", "0");
        });
    }

    /// <summary>
    /// Empty the board. Unconditional, unlike a bulk eject: this is the suite clearing its own
    /// state between scenarios, not a user asking, so a run left unfinished by an earlier
    /// scenario goes too. The archives move to <c>ejected/</c> as ever and the folder is thrown
    /// away with the resource.
    /// </summary>
    public Task ResetBetweenScenarios()
    {
        if (_host == null) return Task.CompletedTask;

        var registry = _host.Services.GetRequiredService<MonitorRunRegistry>();
        foreach (var run in registry.All().ToList()) registry.Remove(run.RunId);

        return Task.CompletedTask;
    }

    public ValueTask BeginScenarioScope() => _scope.Begin();

    public ValueTask EndScenarioScope() => _scope.End();

    public async ValueTask DisposeAsync()
    {
        await _scope.End();
        if (_host != null) await _host.DisposeAsync();

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
