using Bobcat.Runtime;

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
    /// Look up a test resource by type and optional name.
    /// </summary>
    T GetResource<T>(string? name = null) where T : class, ITestResource;

    /// <summary>
    /// Log a message that will be correlated to the current step in results.
    /// </summary>
    void Log(string message);

    /// <summary>
    /// Attach diagnostic data to the current step (SQL queries, HTTP calls, etc).
    /// </summary>
    void AttachDiagnostic(string key, object data);

    /// <summary>
    /// Report interim progress for the currently executing step — a partial result or short
    /// status message — so live renderers can update the step's row before it finishes.
    /// Safe to call from a long-running poll loop; a no-op when no step is executing.
    /// </summary>
    void ReportProgress(StepUpdate update);

    /// <summary>
    /// Record that this scenario observably touched a CLR type — a command it dispatched, an
    /// event it appended or saw emitted, an aggregate it arranged, a message its tracked session
    /// sent, a read-model document it loaded (issue #107). Evidence is observed, never asserted:
    /// call it at the point the type actually crossed the scenario's path, not where a step
    /// merely names it. Accumulates in step order, deduplicated, onto
    /// <see cref="ExecutionResults.TouchedTypes"/> and travels on <c>scenario_finished</c>.
    /// Default no-op so narrow test fakes need not care.
    /// </summary>
    void RecordTouchedType(Type type)
    {
    }

    CancellationToken Cancellation { get; }
}
