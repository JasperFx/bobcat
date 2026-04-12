using Microsoft.Extensions.Hosting;

namespace Bobcat.Runtime;

/// <summary>
/// Marker interface for any test resource that wraps an IHost.
/// Enables Wolverine, Marten, and other extensions to locate the host
/// without knowing the specific resource type (AlbaResource, HostResource, etc).
/// </summary>
public interface IHostResource : ITestResource
{
    IHost Host { get; }
}
