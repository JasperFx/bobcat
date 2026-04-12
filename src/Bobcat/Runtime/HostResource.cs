using Microsoft.Extensions.Hosting;

namespace Bobcat.Runtime;

/// <summary>
/// A test resource that manages an IHost built from a user-provided factory.
/// The factory receives no arguments — the user controls the entire host construction.
/// The host should NOT be pre-started; HostResource.Start() calls StartAsync().
/// </summary>
public class HostResource : IHostResource
{
    private readonly Func<Task<IHost>> _hostFactory;
    private readonly Func<IHost, Task>? _reset;

    public IHost Host { get; private set; } = null!;
    public string Name { get; }

    /// <summary>
    /// Create a HostResource with an async factory that builds and returns the IHost.
    /// </summary>
    public HostResource(Func<Task<IHost>> hostFactory, string? name = null, Func<IHost, Task>? reset = null)
    {
        _hostFactory = hostFactory;
        Name = name ?? "Host";
        _reset = reset;
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

    public async Task ResetBetweenScenarios()
    {
        if (_reset != null)
            await _reset(Host);
    }

    public async ValueTask DisposeAsync()
    {
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
public class HostResource<TProgram> : IHostResource where TProgram : class
{
    private readonly Action<HostApplicationBuilder>? _configure;
    private readonly Func<IHost, Task>? _reset;

    public IHost Host { get; private set; } = null!;
    public string Name { get; }

    public HostResource(Action<HostApplicationBuilder>? configure = null, string? name = null, Func<IHost, Task>? reset = null)
    {
        Name = name ?? typeof(TProgram).Name;
        _configure = configure;
        _reset = reset;
    }

    public async Task Start()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        _configure?.Invoke(builder);
        Host = builder.Build();
        await Host.StartAsync();
    }

    public async Task ResetBetweenScenarios()
    {
        if (_reset != null)
            await _reset(Host);
    }

    public async ValueTask DisposeAsync()
    {
        if (Host != null)
        {
            await Host.StopAsync();
            Host.Dispose();
        }
    }
}
