using Alba;
using Bobcat.Alba;
using Bobcat.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Bobcat.Runtime;

/// <summary>
/// Diagnostics for the Alba host bootstrap footguns documented in docs/sample-wiring.md.
/// </summary>
public static class AlbaResourceDiagnostics
{
    /// <summary>
    /// Turn a content-root failure out of WebApplicationFactory — a bare
    /// <see cref="DirectoryNotFoundException"/> when the root it guessed is not there, or the
    /// <see cref="InvalidOperationException"/> it throws when no solution file is above the test
    /// output — into a clear, actionable configuration error. Other exceptions pass through
    /// unchanged.
    /// </summary>
    public static Exception WrapStartException(Exception ex, string program, string? contentRoot = null)
        => IsContentRootFailure(ex)
            ? new BobcatConfigurationException(ContentRootHelp(program, contentRoot), ex)
            : ex;

    public static bool IsContentRootFailure(Exception ex)
        => ex is DirectoryNotFoundException
           || (ex is InvalidOperationException && ex.Message.StartsWith("Solution root could not be located", StringComparison.Ordinal));

    public static string ContentRootHelp(string program, string? contentRoot = null)
    {
        var used = contentRoot == null ? "" : $" Bobcat resolved it to: {contentRoot}.";
        return $"Alba could not resolve the content root while starting host '{program}'.{used} " +
               "Bobcat resolves it from MvcTestingAppManifest.json in the test output, then " +
               "[assembly: WebApplicationFactoryContentRoot(...)], then the project directory below the solution, " +
               "then the test output directory itself — so reaching this usually means no solution file is above the " +
               "test output, or the directory it found is not the one the host wants. Fix: call " +
               "AlbaResource<TProgram>.WithContentRoot(path), or add " +
               "[assembly: WebApplicationFactoryContentRoot(\"<HostAssemblyName>\", \"<relative path from the test output>\", \"appsettings.json\", \"1\")] " +
               "to the test assembly. See docs/sample-wiring.md footgun 2.";
    }
}

/// <summary>
/// A test resource that wraps an Alba IAlbaHost built from a user-provided factory.
/// Use this when you want full control over IHostBuilder construction rather than
/// bootstrapping from a TProgram entry point.
/// </summary>
public class AlbaResource : IHostResource, IAlbaResource, IRestartableResource
{
    private readonly Func<Task<IAlbaHost>> _factory;
    private readonly Func<IAlbaHost, Task>? _reset;
    private readonly ScenarioScope _scope;
    private IAlbaHost? _albaHost;

    /// <summary>
    /// The underlying IAlbaHost. Use this for Scenario() calls and Alba-specific APIs.
    /// </summary>
    public IAlbaHost AlbaHost => _albaHost
        ?? throw new InvalidOperationException($"AlbaResource '{Name}' has not been started.");

    /// <summary>
    /// IHostResource.Host — IAlbaHost extends IHost, returned directly.
    /// </summary>
    public IHost Host => AlbaHost;

    public IServiceProvider RootServices => _scope.Root;
    public IServiceProvider CurrentServices => _scope.Current;

    public string Name { get; }

    public AlbaResource(Func<Task<IAlbaHost>> factory, string? name = null, Func<IAlbaHost, Task>? reset = null)
    {
        _factory = factory;
        Name = name ?? "AlbaHost";
        _reset = reset;
        _scope = new ScenarioScope(Name, () => _albaHost?.Services);
    }

    public AlbaResource(Func<IAlbaHost> factory, string? name = null, Func<IAlbaHost, Task>? reset = null)
        : this(() => Task.FromResult(factory()), name, reset)
    {
    }

    public async Task Start()
    {
        _albaHost = await _factory();
    }

    /// <inheritdoc cref="IRestartableResource.Restart"/>
    public async Task Restart(CancellationToken token = default)
    {
        var old = _albaHost ?? throw new InvalidOperationException($"AlbaResource '{Name}' has not been started.");

        // The scenario scope belongs to the old container; it has to go before the container does,
        // and come back on the new one so CurrentServices keeps working for the rest of the scenario.
        var scopeWasOpen = _scope.IsOpen;
        await _scope.End();

        _albaHost = null;
        await old.DisposeAsync();

        _albaHost = await _factory();

        if (scopeWasOpen) await _scope.Begin();
    }

    public async Task ResetBetweenScenarios()
    {
        if (_reset != null)
            await _reset(_albaHost!);
    }

    public ValueTask BeginScenarioScope() => _scope.Begin();

    public ValueTask EndScenarioScope() => _scope.End();

    public async ValueTask DisposeAsync()
    {
        await _scope.End();

        if (_albaHost != null)
            await _albaHost.DisposeAsync();
    }
}

/// <summary>
/// A test resource that wraps an Alba IAlbaHost for ASP.NET Core integration testing.
/// Uses AlbaHost.For&lt;TProgram&gt; to bootstrap the host from a TProgram entry point —
/// the same pattern used by WebApplicationFactory. TProgram must be a class in an
/// executable assembly that builds an IHost (typically a Program or Startup class).
/// Implements IHostResource so that Wolverine, Marten, and other extensions can locate
/// the host without knowing the specific resource type.
/// </summary>
public class AlbaResource<TProgram> : IHostResource, IAlbaResource, IRestartableResource where TProgram : class
{
    private readonly Action<IWebHostBuilder>? _configure;
    private readonly IAlbaExtension[] _extensions;
    private readonly Func<IAlbaHost, Task>? _reset;
    private readonly ScenarioScope _scope;
    private IAlbaHost? _albaHost;
    private string? _contentRoot;

    /// <summary>
    /// The underlying IAlbaHost. Use this for Scenario() calls and Alba-specific APIs.
    /// </summary>
    public IAlbaHost AlbaHost => _albaHost
        ?? throw new InvalidOperationException($"AlbaResource '{Name}' has not been started.");

    /// <summary>
    /// IHostResource.Host — IAlbaHost extends IHost, returned directly.
    /// </summary>
    public IHost Host => AlbaHost;

    public IServiceProvider RootServices => _scope.Root;
    public IServiceProvider CurrentServices => _scope.Current;

    public string Name { get; }

    public AlbaResource(string? name = null, Action<IWebHostBuilder>? configure = null,
        Func<IAlbaHost, Task>? reset = null, params IAlbaExtension[] extensions)
    {
        Name = name ?? typeof(TProgram).Name;
        _configure = configure;
        _reset = reset;
        _extensions = extensions;
        _scope = new ScenarioScope(Name, () => _albaHost?.Services);
    }

    /// <summary>
    /// Set the host content root explicitly, bypassing discovery altogether. Rarely needed now:
    /// without it the root is resolved by <see cref="AlbaContentRoot"/> (manifest in the test
    /// output, <c>[WebApplicationFactoryContentRoot]</c>, the project directory below the
    /// solution, then the test output directory), which covers sibling, <c>src/</c>, <c>samples/</c>
    /// and nested-Tests layouts alike. Use this when the host wants a directory none of those are.
    /// </summary>
    public AlbaResource<TProgram> WithContentRoot(string contentRoot)
    {
        _contentRoot = contentRoot;
        return this;
    }

    /// <summary>
    /// How the content root was (or will be) decided for this resource — explicit
    /// <see cref="WithContentRoot"/>, or the outcome of <see cref="AlbaContentRoot.Resolve(System.Reflection.Assembly)"/>.
    /// Evaluated on each start, so it reflects the files present at that moment.
    /// </summary>
    public AlbaContentRoot.Resolution ContentRoot => _contentRoot != null
        ? new AlbaContentRoot.Resolution(_contentRoot, "WithContentRoot")
        : AlbaContentRoot.Resolve(typeof(TProgram).Assembly);

    public async Task Start()
    {
        _albaHost = await boot();
    }

    /// <inheritdoc cref="IRestartableResource.Restart"/>
    /// <remarks>
    /// Boots the new host exactly as <see cref="Start"/> did — same <c>configure</c> callback,
    /// same extensions, same content root — so a restarted application is the same application
    /// with a fresh container, not a differently configured one.
    /// </remarks>
    public async Task Restart(CancellationToken token = default)
    {
        var old = _albaHost ?? throw new InvalidOperationException($"AlbaResource '{Name}' has not been started.");

        // The scenario scope belongs to the old container; it has to go before the container does,
        // and come back on the new one so CurrentServices keeps working for the rest of the scenario.
        var scopeWasOpen = _scope.IsOpen;
        await _scope.End();

        _albaHost = null;
        await old.DisposeAsync();

        _albaHost = await boot();

        if (scopeWasOpen) await _scope.Begin();
    }

    private async Task<IAlbaHost> boot()
    {
        var contentRoot = ContentRoot;
        var configure = composeConfigure(contentRoot.Path);
        try
        {
            return configure != null
                ? await global::Alba.AlbaHost.For<TProgram>(configure, _extensions)
                : await global::Alba.AlbaHost.For<TProgram>(_extensions);
        }
        catch (Exception ex)
        {
            throw AlbaResourceDiagnostics.WrapStartException(ex, typeof(TProgram).Name, contentRoot.ToString());
        }
    }

    private Action<IWebHostBuilder>? composeConfigure(string? contentRoot)
    {
        if (contentRoot == null) return _configure;

        var userConfigure = _configure;
        return builder =>
        {
            // WebApplicationFactory sets its own guess first and our callback runs after it, so
            // this is the value the host builds with. The user's configure runs last and may
            // still override it.
            builder.UseContentRoot(contentRoot);
            userConfigure?.Invoke(builder);
        };
    }

    public async Task ResetBetweenScenarios()
    {
        if (_reset != null)
            await _reset(_albaHost!);
    }

    public ValueTask BeginScenarioScope() => _scope.Begin();

    public ValueTask EndScenarioScope() => _scope.End();

    public async ValueTask DisposeAsync()
    {
        await _scope.End();

        if (_albaHost != null)
            await _albaHost.DisposeAsync();
    }
}
