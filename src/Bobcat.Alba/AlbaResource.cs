using Alba;
using Bobcat.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Bobcat.Alba;

/// <summary>
/// Wraps an IAlbaHost as a Bobcat ITestResource.
/// Use AlbaResource.ForApp() for a self-contained test web app, or
/// AlbaResource&lt;TProgram&gt; to spin up an existing ASP.NET Core application.
/// </summary>
public class AlbaResource<TProgram> : ITestResource where TProgram : class
{
    private readonly IAlbaExtension[] _extensions;

    public IAlbaHost Host { get; private set; } = null!;
    public IScenarioResult? LastResult { get; internal set; }
    public string Name { get; }

    public AlbaResource(string? name = null, params IAlbaExtension[] extensions)
    {
        Name = name ?? typeof(TProgram).Name;
        _extensions = extensions;
    }

    public async Task Start()
    {
        Host = await AlbaHost.For<TProgram>(_extensions);
    }

    public Task ResetBetweenScenarios()
    {
        LastResult = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }
}

/// <summary>
/// A self-contained AlbaResource that configures a minimal ASP.NET Core app inline,
/// without requiring a separate application project or entry point.
/// </summary>
public class AlbaResource : ITestResource
{
    private readonly Func<WebApplicationBuilder> _builderFactory;
    private readonly Action<WebApplication> _configureRoutes;
    private readonly IAlbaExtension[] _extensions;

    public IAlbaHost Host { get; private set; } = null!;
    public IScenarioResult? LastResult { get; internal set; }
    public string Name { get; }

    public AlbaResource(
        string name,
        Func<WebApplicationBuilder> builderFactory,
        Action<WebApplication> configureRoutes,
        params IAlbaExtension[] extensions)
    {
        Name = name;
        _builderFactory = builderFactory;
        _configureRoutes = configureRoutes;
        _extensions = extensions;
    }

    public static AlbaResource For(
        string name,
        Action<WebApplication> configureRoutes,
        params IAlbaExtension[] extensions)
        => new(name, WebApplication.CreateBuilder, configureRoutes, extensions);

    public static AlbaResource For(
        string name,
        Action<IServiceCollection> configureServices,
        Action<WebApplication> configureRoutes,
        params IAlbaExtension[] extensions)
        => new(name, () =>
        {
            var builder = WebApplication.CreateBuilder();
            configureServices(builder.Services);
            return builder;
        }, configureRoutes, extensions);

    public async Task Start()
    {
        Host = await AlbaHost.For(_builderFactory(), _configureRoutes, _extensions);
    }

    public Task ResetBetweenScenarios()
    {
        LastResult = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }
}
