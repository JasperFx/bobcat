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
    /// Resolve a service directly from the IHost's DI container.
    /// </summary>
    public static T GetHostService<T>(this IStepContext context, string? name = null) where T : notnull
        => context.GetHost(name).Services.GetRequiredService<T>();
}
