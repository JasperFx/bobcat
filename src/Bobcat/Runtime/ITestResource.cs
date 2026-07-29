namespace Bobcat.Runtime;

/// <summary>
/// A named test resource — database, IHost, Docker container, message broker, etc.
/// Resources are managed by TestSuite: started once at suite start, reset between
/// scenarios, torn down at suite end.
/// </summary>
public interface ITestResource : IAsyncDisposable
{
    /// <summary>
    /// Unique name for this resource. Used for lookup when multiple resources
    /// of the same type exist (e.g., two Alba hosts for cross-service testing).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Called once at suite start. Failure here wraps in SpecCatastrophicException.
    /// </summary>
    Task Start();

    /// <summary>
    /// Called between each scenario. Use to reset state (truncate tables,
    /// purge queues, clear tracked sessions, etc).
    /// </summary>
    Task ResetBetweenScenarios();

    /// <summary>
    /// Validate that this resource is usable, throwing with a diagnostic message if not.
    /// Runs during preflight, before any test executes.
    /// </summary>
    /// <remarks>
    /// Default is a no-op so existing resources are unaffected. Deliberately mirrors
    /// <c>JasperFx.Resources.IStatefulResource.Check</c> — same verb, same "throw to fail"
    /// contract — so a resource can satisfy both without adapting.
    /// </remarks>
    Task Check(CancellationToken token) => Task.CompletedTask;
}

/// <summary>
/// A resource whose state cannot be reliably drained the way a database is truncated — message
/// brokers, mostly. Recycling throws the underlying container or process away and stands a fresh
/// one up.
/// </summary>
/// <remarks>
/// <para>
/// <c>Recycle</c> is deliberately a third verb alongside <see cref="ITestResource.ResetBetweenScenarios"/>
/// (clean the state) and <c>DisposeAsync</c> (final teardown). Reset assumes the thing still
/// works; recycle assumes it does not.
/// </para>
/// <para>
/// <strong>Recyclable resources belong to the supervisor, not a worker.</strong> A worker cannot
/// restart the broker it is about to be replaced alongside, which is the whole reason
/// <c>RetryAfterRecycle</c> lives above the process boundary. Register these with
/// <c>Supervisor.AddRecyclableResource</c>.
/// </para>
/// </remarks>
public interface IRecyclableResource : ITestResource
{
    /// <summary>Throw the underlying container/process away and stand a fresh one up.</summary>
    Task Recycle(CancellationToken token = default);
}
