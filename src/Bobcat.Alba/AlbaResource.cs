using Alba;
using Bobcat.Runtime;
using Microsoft.AspNetCore.Hosting;

namespace Bobcat.Alba;

/// <summary>
/// A Bobcat test resource that wraps an Alba IAlbaHost for HTTP integration testing.
/// TProgram is the entry point type of the web application under test.
/// </summary>
public class AlbaResource<TProgram> : ITestResource where TProgram : class
{
    private readonly Action<IWebHostBuilder>? _configure;
    private readonly IAlbaExtension[] _extensions;

    public IAlbaHost Host { get; private set; } = null!;

    /// <summary>
    /// The result of the most recent Scenario() call. Null until first scenario executes.
    /// </summary>
    public IScenarioResult? LastResult { get; internal set; }

    public string Name { get; }

    public AlbaResource(string? name = null, Action<IWebHostBuilder>? configure = null, params IAlbaExtension[] extensions)
    {
        Name = name ?? typeof(TProgram).Name;
        _configure = configure;
        _extensions = extensions;
    }

    // Internal constructor for testing — accepts a pre-built host
    internal AlbaResource(string name, IAlbaHost host)
    {
        Name = name;
        Host = host;
        _configure = null;
        _extensions = [];
    }

    public async Task Start()
    {
        Host = _configure != null
            ? await AlbaHost.For<TProgram>(_configure, _extensions)
            : await AlbaHost.For<TProgram>(_extensions);
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
