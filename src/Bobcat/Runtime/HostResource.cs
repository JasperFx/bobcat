using Microsoft.Extensions.Hosting;

namespace Bobcat.Runtime;

/// <summary>
/// A test resource that manages an IHost built from a user-provided factory.
/// The factory receives no arguments — the user controls the entire host construction.
/// The host should NOT be pre-started; HostResource.Start() calls StartAsync().
/// </summary>
public class HostResource : IHostResource, IRestartableResource
{
    private readonly Func<Task<IHost>> _hostFactory;
    private readonly Func<IHost, Task>? _reset;
    private readonly ScenarioScope _scope;

    public IHost Host { get; private set; } = null!;
    public string Name { get; }

    public IServiceProvider RootServices => _scope.Root;
    public IServiceProvider CurrentServices => _scope.Current;

    /// <summary>
    /// Create a HostResource with an async factory that builds and returns the IHost.
    /// </summary>
    public HostResource(Func<Task<IHost>> hostFactory, string? name = null, Func<IHost, Task>? reset = null)
    {
        _hostFactory = hostFactory;
        Name = name ?? "Host";
        _reset = reset;
        _scope = new ScenarioScope(Name, () => Host?.Services);
    }

    /// <summary>
    /// Convenience overload for synchronous host factories.
    /// </summary>
    public HostResource(Func<IHost> hostFactory, string? name = null, Func<IHost, Task>? reset = null)
        : this(() => Task.FromResult(hostFactory()), name, reset)
    {
    }

    public async Task Start()
    {
        Host = await _hostFactory();
        await Host.StartAsync();
    }

    /// <inheritdoc cref="IRestartableResource.Restart"/>
    public async Task Restart(CancellationToken token = default)
    {
        var old = Host ?? throw new InvalidOperationException($"HostResource '{Name}' has not been started.");

        // The scenario scope belongs to the old container; it has to go before the container does,
        // and come back on the new one so CurrentServices keeps working for the rest of the scenario.
        var scopeWasOpen = _scope.IsOpen;
        await _scope.End();

        await old.StopAsync(token);
        old.Dispose();
        Host = null!;

        Host = await _hostFactory();
        await Host.StartAsync(token);

        if (scopeWasOpen) await _scope.Begin();
    }

    public async Task ResetBetweenScenarios()
    {
        if (_reset != null)
            await _reset(Host);
    }

    public ValueTask BeginScenarioScope() => _scope.Begin();

    public ValueTask EndScenarioScope() => _scope.End();

    public async ValueTask DisposeAsync()
    {
        await _scope.End();

        if (Host != null)
        {
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}

/// <summary>
/// A test resource that builds an IHost using Host.CreateApplicationBuilder and a
/// user-provided configuration callback. The TProgram type parameter is used as a
/// marker for resource lookup — e.g., GetResource&lt;HostResource&lt;MyApp&gt;&gt;().
/// </summary>
public class HostResource<TProgram> : IHostResource, IRestartableResource where TProgram : class
{
    private readonly Action<HostApplicationBuilder>? _configure;
    private readonly Func<IHost, Task>? _reset;
    private readonly ScenarioScope _scope;

    public IHost Host { get; private set; } = null!;
    public string Name { get; }

    public IServiceProvider RootServices => _scope.Root;
    public IServiceProvider CurrentServices => _scope.Current;

    public HostResource(Action<HostApplicationBuilder>? configure = null, string? name = null, Func<IHost, Task>? reset = null)
    {
        Name = name ?? typeof(TProgram).Name;
        _configure = configure;
        _reset = reset;
        _scope = new ScenarioScope(Name, () => Host?.Services);
    }

    public async Task Start()
    {
        Host = build();
        await Host.StartAsync();
    }

    /// <inheritdoc cref="IRestartableResource.Restart"/>
    public async Task Restart(CancellationToken token = default)
    {
        var old = Host ?? throw new InvalidOperationException($"HostResource '{Name}' has not been started.");

        var scopeWasOpen = _scope.IsOpen;
        await _scope.End();

        await old.StopAsync(token);
        old.Dispose();
        Host = null!;

        Host = build();
        await Host.StartAsync(token);

        if (scopeWasOpen) await _scope.Begin();
    }

    private IHost build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        _configure?.Invoke(builder);
        return builder.Build();
    }

    public async Task ResetBetweenScenarios()
    {
        if (_reset != null)
            await _reset(Host);
    }

    public ValueTask BeginScenarioScope() => _scope.Begin();

    public ValueTask EndScenarioScope() => _scope.End();

    public async ValueTask DisposeAsync()
    {
        await _scope.End();

        if (Host != null)
        {
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}
