using Bobcat.Engine;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;

namespace Bobcat.Wolverine;

public static class WolverineStepContextExtensions
{
    /// <summary>
    /// Retrieve the WolverineResource by optional name.
    /// </summary>
    public static WolverineResource GetWolverineResource(this IStepContext context, string? name = null)
        => context.GetResource<WolverineResource>(name);

    /// <summary>
    /// Retrieve the underlying IHost from the WolverineResource.
    /// </summary>
    public static IHost GetWolverineHost(this IStepContext context, string? name = null)
        => context.GetWolverineResource(name).Host;

    /// <summary>
    /// Invoke a message and wait for all cascading activity to finish.
    /// Stores the resulting session on the resource for later assertion.
    /// </summary>
    public static async Task<ITrackedSession> InvokeMessageAndWaitAsync(
        this IStepContext context,
        object message,
        string? name = null,
        int timeoutInMilliseconds = 5000)
    {
        var resource = context.GetWolverineResource(name);
        var session = await resource.Host.InvokeMessageAndWaitAsync(message, timeoutInMilliseconds);
        resource.LastSession = session;
        return session;
    }

    /// <summary>
    /// Invoke a message and wait, returning both the tracked session and the response.
    /// Stores the resulting session on the resource for later assertion.
    /// </summary>
    public static async Task<(ITrackedSession Session, TResponse? Response)> InvokeMessageAndWaitAsync<TResponse>(
        this IStepContext context,
        object message,
        string? name = null,
        int timeoutInMilliseconds = 5000)
    {
        var resource = context.GetWolverineResource(name);
        var (session, response) = await resource.Host.InvokeMessageAndWaitAsync<TResponse>(message, timeoutInMilliseconds);
        resource.LastSession = session;
        return (session, response);
    }

    /// <summary>
    /// Execute an arbitrary action against IMessageContext and wait for all cascading activity.
    /// Stores the resulting session on the resource for later assertion.
    /// </summary>
    public static async Task<ITrackedSession> ExecuteAndWaitAsync(
        this IStepContext context,
        Func<IMessageContext, Task> action,
        string? name = null,
        int timeoutInMilliseconds = 5000)
    {
        var resource = context.GetWolverineResource(name);
        var session = await resource.Host.ExecuteAndWaitAsync(action, timeoutInMilliseconds);
        resource.LastSession = session;
        return session;
    }

    /// <summary>
    /// Send a message (fire-and-forget) and wait for all cascading activity.
    /// Stores the resulting session on the resource for later assertion.
    /// </summary>
    public static async Task<ITrackedSession> SendMessageAndWaitAsync<T>(
        this IStepContext context,
        T message,
        string? name = null,
        int timeoutInMilliseconds = 5000)
    {
        var resource = context.GetWolverineResource(name);
        var session = await resource.Host.SendMessageAndWaitAsync(message, timeoutInMilliseconds: timeoutInMilliseconds);
        resource.LastSession = session;
        return session;
    }

    /// <summary>
    /// Access the ITrackedSession from the most recent tracked operation on this resource.
    /// Throws if no tracked operation has been performed.
    /// </summary>
    public static ITrackedSession LastTrackedSession(this IStepContext context, string? name = null)
    {
        var resource = context.GetWolverineResource(name);
        return resource.LastSession
               ?? throw new InvalidOperationException(
                   $"No tracked session available on WolverineResource '{resource.Name}'. Run a tracked operation first.");
    }
}
