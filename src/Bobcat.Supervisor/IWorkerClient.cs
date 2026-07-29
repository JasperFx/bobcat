namespace Bobcat.Supervisor;

/// <summary>How a worker reported one test.</summary>
public enum WorkerTestState
{
    Passed,
    Failed,
    Error,
    Skipped,
    Timeout,
    Cancelled,

    /// <summary>
    /// The supervisor asked for this test but never heard an answer — almost always because the
    /// worker died mid-run. Deliberately NOT "failed": the #43 spike found that how much
    /// completed work reaches the parent before a crash is not guaranteed, so treating silence
    /// as failure would invent results.
    /// </summary>
    Indeterminate
}

/// <summary>A test as the worker described it at discovery.</summary>
public sealed record WorkerTest(string Uid, string DisplayName)
{
    /// <summary>
    /// Framework metadata — xUnit <c>[Trait]</c>, tUnit <c>[Property]</c>, Bobcat tags. The #43
    /// spike established this is the only metadata channel that survives every front-end intact,
    /// so retry policy keys off these rather than exception types.
    /// </summary>
    public IReadOnlyDictionary<string, string> Traits { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One test's outcome from one attempt.</summary>
public sealed record WorkerOutcome(string Uid, string DisplayName, WorkerTestState State)
{
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public TimeSpan? Duration { get; init; }

    public IReadOnlyDictionary<string, string> Traits { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool Succeeded => State is WorkerTestState.Passed or WorkerTestState.Skipped;

    /// <summary>
    /// True when the framework said an assertion disagreed rather than an exception escaping.
    /// Both xUnit and tUnit make this distinction natively; it is the primary classification
    /// signal available to a policy.
    /// </summary>
    public bool IsAssertionFailure => State == WorkerTestState.Failed;
}

/// <summary>The result of asking a worker to run a set of tests.</summary>
public sealed record WorkerRunResult(IReadOnlyList<WorkerOutcome> Outcomes)
{
    /// <summary>
    /// Set when the worker process died or the connection broke mid-run. Written to be read by
    /// a human at 2am: it names the exit code and the worker's last words, not just
    /// "the connection closed".
    /// </summary>
    public string? Fault { get; init; }

    /// <summary>The worker's exit code, when it had exited by the time we looked.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The tail of the worker's standard error — where an unhandled exception lands.</summary>
    public string? StandardError { get; init; }

    public bool Crashed => Fault is not null;
}

/// <summary>
/// The supervisor's entire view of a worker. Every wire detail — JSON-RPC framing, MTP node
/// shapes, parameter names — lives behind this interface.
/// </summary>
/// <remarks>
/// The #43 spike's top-listed risk was that MTP's server mode is undocumented and has shifted
/// between versions. This seam is the mitigation: if the protocol moves, or we ever need the
/// native-protocol fallback, it is one implementation rather than a refactor.
/// </remarks>
public interface IWorkerClient : IAsyncDisposable
{
    /// <summary>Enumerate without executing.</summary>
    Task<IReadOnlyList<WorkerTest>> Discover(CancellationToken ct = default);

    /// <summary>
    /// Run the given tests, or everything when <paramref name="uids"/> is null.
    /// </summary>
    /// <remarks>
    /// Implementations MUST verify that a filtered request actually filtered. The spike found
    /// that MTP silently ignores an unrecognised subset parameter and runs the entire suite,
    /// which a parent cannot distinguish from a filter matching everything — and a retry that
    /// silently re-runs everything would launder unrelated failures into the attempt.
    /// </remarks>
    Task<WorkerRunResult> Run(IReadOnlyList<string>? uids = null, CancellationToken ct = default);
}

/// <summary>Creates worker processes. One call, one fresh process.</summary>
public interface IWorkerFactory
{
    /// <summary>A short name for the worker, used in reporting.</summary>
    string Description { get; }

    Task<IWorkerClient> Launch(CancellationToken ct = default);
}
