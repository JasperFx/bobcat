using Alba;
using Bobcat.Runtime;
using JasperFx.CommandLine;
using Microsoft.AspNetCore.Hosting;

namespace MeetingGroupMonolith.Tests;

/// <summary>
/// The application under test, hosted in memory by Alba.
/// </summary>
/// <remarks>
/// <b>Duplicated in every sample, deliberately.</b> Bobcat ships no Alba integration, so a sample
/// that wants one writes it. Nine identical copies is not an oversight — it is the measurement of
/// what the deleted <c>Bobcat.Alba</c> was actually carrying.
/// </remarks>
public sealed class WebApp : ITestResource
{
    private readonly Func<IAlbaHost, Task>? _reset;
    private IAlbaHost? _host;

    public WebApp(Func<IAlbaHost, Task>? reset = null) => _reset = reset;

    public string Name => "app";

    public IAlbaHost Host => _host
        ?? throw new InvalidOperationException("The Alba host has not been started yet.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // A JasperFx host built by WebApplicationFactory would otherwise run its command line and
        // never hand the host back.
        if (!JasperFxEnvironment.AutoStartHost) JasperFxEnvironment.AutoStartHost = true;

        // WebApplicationFactory guesses the content root as <solution>/<assembly name>, which is
        // wrong for every project that does not sit directly under the solution — samples/X/ here.
        var contentRoot = AlbaContentRoot.Resolve(typeof(Program).Assembly);

        _host = contentRoot.Path is { } path
            ? await AlbaHost.For<Program>(builder => builder.UseContentRoot(path))
            : await AlbaHost.For<Program>();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var host = _host;
        _host = null;
        if (host != null) await host.DisposeAsync();
    }

    public Task ResetBetweenScenarios() => _reset?.Invoke(Host) ?? Task.CompletedTask;
}
