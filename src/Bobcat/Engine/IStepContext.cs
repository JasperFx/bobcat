namespace Bobcat.Engine;

/// <summary>
/// The context visible to grammar/step execution code.
/// Intentionally narrow — steps should not call engine lifecycle methods.
/// </summary>
public interface IStepContext
{
    string SpecId { get; }

    /// <summary>
    /// Resolve a service from the test system's DI container.
    /// </summary>
    T GetService<T>() where T : notnull;

    /// <summary>
    /// Log a message that will be correlated to the current step in results.
    /// </summary>
    void Log(string message);

    /// <summary>
    /// Attach diagnostic data to the current step (SQL queries, HTTP calls, etc).
    /// </summary>
    void AttachDiagnostic(string key, object data);

    CancellationToken Cancellation { get; }
}
