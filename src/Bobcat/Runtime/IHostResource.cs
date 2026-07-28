using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bobcat.Runtime;

/// <summary>
/// Any test resource that wraps an IHost — and therefore owns a DI container.
/// Enables Wolverine, Marten, and other extensions to locate the host without knowing
/// the specific resource type (AlbaResource, HostResource, etc).
///
/// The resource owns BOTH providers: <see cref="RootServices"/> (the host's root container)
/// and <see cref="CurrentServices"/> (the per-scenario scope). The runner never touches
/// <c>IServiceScopeFactory</c> — it just brackets each scenario with
/// <see cref="BeginScenarioScope"/> / <see cref="EndScenarioScope"/>.
/// </summary>
public interface IHostResource : ITestResource
{
    IHost Host { get; }

    /// <summary>
    /// The host's root service provider — the same thing as <c>Host.Services</c>.
    /// Use for singletons and for global/static setup that runs outside a scenario.
    /// </summary>
    IServiceProvider RootServices { get; }

    /// <summary>
    /// The active per-scenario scope's provider. THROWS when no scenario scope is open —
    /// there is deliberately no silent fallback to the root container, because that is
    /// exactly the captive-dependency trap this abstraction exists to prevent.
    /// </summary>
    IServiceProvider CurrentServices { get; }

    /// <summary>Open a fresh DI scope for the scenario about to run.</summary>
    ValueTask BeginScenarioScope();

    /// <summary>Dispose the current scenario's DI scope. Safe to call when none is open.</summary>
    ValueTask EndScenarioScope();
}

/// <summary>
/// Shared implementation of the per-scenario DI scope that every <see cref="IHostResource"/>
/// composes in. Keeps the scope lifecycle in one place rather than repeating it across
/// HostResource, HostResource&lt;T&gt;, AlbaResource, and AlbaResource&lt;T&gt;.
/// </summary>
public sealed class ScenarioScope
{
    private readonly string _resourceName;
    private readonly Func<IServiceProvider?> _rootAccessor;
    private IServiceScope? _scope;

    public ScenarioScope(string resourceName, Func<IServiceProvider?> rootAccessor)
    {
        _resourceName = resourceName;
        _rootAccessor = rootAccessor;
    }

    public IServiceProvider Root => _rootAccessor()
        ?? throw new InvalidOperationException(
            $"Resource '{_resourceName}' has not been started, so its service provider is not available yet.");

    public IServiceProvider Current => _scope?.ServiceProvider
        ?? throw new InvalidOperationException(
            $"No scenario scope is open on resource '{_resourceName}'. Scoped services are only " +
            "resolvable while a scenario is running. Use RootServices (or GetRootService<T>()) for " +
            "singletons and for setup that runs outside a scenario.");

    public bool IsOpen => _scope != null;

    public async ValueTask Begin()
    {
        // Defensive: a scenario that aborted without an End must not leak its scope.
        await End();
        _scope = Root.CreateScope();
    }

    public async ValueTask End()
    {
        var scope = _scope;
        _scope = null;
        if (scope == null) return;

        if (scope is IAsyncDisposable async)
            await async.DisposeAsync();
        else
            scope.Dispose();
    }

    /// <summary>Create a child scope nested under the current scenario scope.</summary>
    public IServiceScope CreateChildScope() => Current.CreateScope();
}
