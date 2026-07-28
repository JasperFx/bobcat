using Bobcat.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bobcat.Runtime;

public static class HostResourceExtensions
{
    /// <summary>
    /// Get the IHost from any registered IHostResource (AlbaResource, HostResource, etc).
    /// If multiple IHostResource registrations exist, specify a name to disambiguate.
    /// </summary>
    public static IHost GetHost(this IStepContext context, string? name = null)
        => context.GetResource<IHostResource>(name).Host;

    /// <summary>
    /// Resolve a service from the CURRENT SCENARIO's DI scope. This is the default for step
    /// and grammar code: a scoped service (Marten's IDocumentSession, an EF DbContext) is the
    /// same instance for every step in one scenario and a fresh one in the next.
    /// Throws when no scenario scope is open — use <see cref="GetRootService{T}"/> for
    /// singletons and setup that runs outside a scenario.
    /// </summary>
    public static T GetHostService<T>(this IStepContext context, string? name = null) where T : notnull
        => context.GetResource<IHostResource>(name).CurrentServices.GetRequiredService<T>();

    /// <summary>
    /// Resolve a service from the host's ROOT container, bypassing the scenario scope.
    /// The explicit escape hatch for singletons and global/static setup.
    /// </summary>
    public static T GetRootService<T>(this IStepContext context, string? name = null) where T : notnull
        => context.GetResource<IHostResource>(name).RootServices.GetRequiredService<T>();

    /// <summary>
    /// Resolve a keyed service from the current scenario's DI scope.
    /// </summary>
    public static T GetKeyedHostService<T>(this IStepContext context, object? key, string? name = null) where T : notnull
        => context.GetResource<IHostResource>(name).CurrentServices.GetRequiredKeyedService<T>(key);

    /// <summary>
    /// The current scenario's service provider for a host resource — for code that needs the
    /// provider itself rather than a single resolution.
    /// </summary>
    public static IServiceProvider ScenarioServices(this IStepContext context, string? name = null)
        => context.GetResource<IHostResource>(name).CurrentServices;

    /// <summary>
    /// The host's root service provider.
    /// </summary>
    public static IServiceProvider RootServices(this IStepContext context, string? name = null)
        => context.GetResource<IHostResource>(name).RootServices;

    /// <summary>
    /// Create a child DI scope nested UNDER the current scenario scope. Backs the
    /// <c>[NewScope]</c> / <c>[ScopePerRow]</c> escape hatches — the host resource still owns
    /// the scenario scope; this just nests inside it. Dispose the returned scope when done.
    /// </summary>
    public static IServiceScope CreateChildScope(this IStepContext context, string? name = null)
        => context.GetResource<IHostResource>(name).CurrentServices.CreateScope();
}
