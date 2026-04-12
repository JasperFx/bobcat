using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Bobcat.Wolverine;

/// <summary>
/// Extension methods for IStepContext that delegate to Wolverine's message tracking APIs.
/// These work against any registered IHostResource (HostResource, AlbaResource, etc)
/// so fixtures do not need to reference a Wolverine-specific resource type.
/// </summary>
public static class WolverineStepContextExtensions
{
    private static IHost GetWolverineHost(IStepContext context, string? resourceName)
        => context.GetResource<IHostResource>(resourceName).Host;

    /// <summary>
    /// Invoke a message and wait for it and all cascading messages to complete.
    /// </summary>
    public static Task<ITrackedSession> InvokeMessageAndWaitAsync(
        this IStepContext context,
        object message,
        string? resourceName = null,
        int timeoutInMilliseconds = 5000)
        => GetWolverineHost(context, resourceName)
            .InvokeMessageAndWaitAsync(message, timeoutInMilliseconds);

    /// <summary>
    /// Invoke a message expecting a return value, and wait for all cascading messages to complete.
    /// </summary>
    public static Task<(ITrackedSession, T?)> InvokeMessageAndWaitAsync<T>(
        this IStepContext context,
        object message,
        string? resourceName = null,
        int timeoutInMilliseconds = 5000)
        => GetWolverineHost(context, resourceName)
            .InvokeMessageAndWaitAsync<T>(message, timeoutInMilliseconds);

    /// <summary>
    /// Send a message and wait for it and all cascading messages to complete.
    /// </summary>
    public static Task<ITrackedSession> SendMessageAndWaitAsync<T>(
        this IStepContext context,
        T message,
        string? resourceName = null,
        int timeoutInMilliseconds = 5000)
        => GetWolverineHost(context, resourceName)
            .SendMessageAndWaitAsync(message, null, timeoutInMilliseconds);

    /// <summary>
    /// Start a tracked activity session on the Wolverine host for manual coordination.
    /// </summary>
    public static TrackedSessionConfiguration TrackActivity(
        this IStepContext context,
        string? resourceName = null)
        => GetWolverineHost(context, resourceName).TrackActivity();
}
