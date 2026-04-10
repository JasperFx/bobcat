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
}
