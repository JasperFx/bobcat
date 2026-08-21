using JasperFx;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bobcat.Alba.SampleJasperFxWeb;

/// <summary>
/// A host whose Main ends in <c>RunJasperFxCommands</c>, exactly as a Wolverine or Marten
/// application's does. An explicit class rather than top-level statements, so the test project
/// that references this one never sees two global-namespace <c>Program</c> types
/// (docs/sample-wiring.md footgun 1).
/// </summary>
public class Program
{
    public static Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<StartCounter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StartCounter>());
        var app = builder.Build();

        app.MapGet("/hello", () => "Hello from SampleJasperFxWeb");
        // How many times the host's hosted services have been started — the observable
        // difference between the command runner starting the host itself and leaving it alone.
        app.MapGet("/starts", (StartCounter counter) => counter.Starts);

        return app.RunJasperFxCommands(args);
    }
}

/// <summary>Counts <see cref="IHostedService.StartAsync"/> calls on this host instance.</summary>
public sealed class StartCounter : IHostedService
{
    private int _starts;
    public int Starts => _starts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _starts);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
