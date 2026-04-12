using Bobcat.Runtime;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;

namespace Bobcat.Wolverine;

/// <summary>
/// A Bobcat test resource wrapping a Wolverine-enabled IHost.
/// Automatically applies DurabilityMode.Solo and disables external transports
/// so the host is suitable for in-process integration testing.
///
/// The factory must NOT call UseWolverine() — WolverineResource owns that call
/// and merges in the test-specific Wolverine configuration.
/// </summary>
public class WolverineResource : ITestResource
{
    private readonly Func<IHostBuilder> _factory;
    private readonly Action<WolverineOptions>? _configure;
    private IHost? _host;

    public WolverineResource(string name, Func<IHostBuilder> factory, Action<WolverineOptions>? configure = null)
    {
        Name = name;
        _factory = factory;
        _configure = configure;
    }

    public WolverineResource(Func<IHostBuilder> factory, Action<WolverineOptions>? configure = null)
        : this("wolverine", factory, configure)
    {
    }

    public string Name { get; }

    public IHost Host => _host
        ?? throw new InvalidOperationException($"WolverineResource '{Name}' has not been started.");

    /// <summary>
    /// The session from the most recent tracked operation. Set by the IStepContext extensions.
    /// Cleared on ResetBetweenScenarios.
    /// </summary>
    public ITrackedSession? LastSession { get; set; }

    public async Task Start()
    {
        var builder = _factory();

        builder.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            _configure?.Invoke(opts);
        });

        builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());

        _host = builder.Build();
        await _host.StartAsync();
    }

    public Task ResetBetweenScenarios()
    {
        LastSession = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
    }
}
