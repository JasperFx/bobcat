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

/// <summary>
/// One live per-test state change from a worker mid-run — the tap on MTP's
/// <c>testing/testUpdates/tests</c> stream that <see cref="MtpWorkerClient"/> used to discard
/// (issue #99). A worker reports each test at least twice: in progress when it starts, then its
/// verdict. The terminal update is the same fact <see cref="WorkerRunResult.Outcomes"/> carries
/// at the end; the in-progress one is what only this stream has.
/// </summary>
public sealed record WorkerTestUpdate(string Uid, string DisplayName, string ExecutionState)
{
    /// <summary>True while the worker is running this test, before it has a verdict.</summary>
    public bool InProgress => ExecutionState == "in-progress";

    /// <summary>
    /// The verdict when this update is terminal; null while in progress (or for a state this
    /// supervisor does not classify, which <see cref="WorkerOutcome"/> would call indeterminate).
    /// </summary>
    public WorkerTestState? State { get; init; }

    /// <summary>Framework metadata on the node — same channel as <see cref="WorkerOutcome.Traits"/>.</summary>
    public IReadOnlyDictionary<string, string> Traits { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Subscribe to live per-test state changes while <see cref="Run"/> is in flight. Default is
    /// a no-op — a client that cannot observe its worker mid-run simply never calls back, and
    /// the supervisor learns the outcomes from <see cref="Run"/>'s result as before. Handlers
    /// may be invoked from the client's I/O thread and must not throw.
    /// </summary>
    void OnTestUpdate(Action<WorkerTestUpdate> handler)
    {
    }
}

/// <summary>What the supervisor is about to launch a worker for.</summary>
public enum WorkerPurpose
{
    /// <summary>Enumerating tests without running them. Throwaway.</summary>
    Discovery,

    /// <summary>One lane of the parallel pool, running a share of the batched tests.</summary>
    Lane,

    /// <summary>A dedicated process running one test alone.</summary>
    Isolated,

    /// <summary>A dedicated process for a test whose resources were just recycled.</summary>
    Recycled
}

/// <summary>
/// Which worker is being launched, so a caller can shape its environment.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam <c>CLAUDE.md</c> deferred under the name <c>IWorkerLauncher</c>. The
/// requirement it was waiting for — pointing each parallel worker at its own database — turned
/// out to need a launch <em>context</em> rather than a launcher abstraction, so the parameter is
/// all that was added.
/// </para>
/// <para>
/// <see cref="Lane"/> is bounded by <c>MaxParallelWorkers</c> and is the slot number to key
/// per-worker resources off — a database name, a schema prefix, a port. Discovery, isolated and
/// recycled launches all report lane 0: they never run while the pool is running, so reusing the
/// first slot cannot collide, and it keeps the number of databases a suite needs equal to the
/// number of workers it asked for rather than the number of processes it happened to start.
/// </para>
/// </remarks>
public sealed record WorkerLaunchContext(int Lane, WorkerPurpose Purpose)
{
    public static readonly WorkerLaunchContext Discovery = new(0, WorkerPurpose.Discovery);

    /// <summary>
    /// Run-scoped environment the supervisor wants every worker to inherit — today the
    /// monitor grouping pair (<c>BOBCAT_RUN_ID</c> + <c>BOBCAT_RUN_OWNER</c>). The lowest
    /// layer of the environment stack: a factory's shared environment and its per-worker
    /// <c>EnvironmentFor</c> both override it, so the supervisor proposes and the factory
    /// disposes.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>Creates worker processes. One call, one fresh process.</summary>
public interface IWorkerFactory
{
    /// <summary>A short name for the worker, used in reporting.</summary>
    string Description { get; }

    Task<IWorkerClient> Launch(WorkerLaunchContext context, CancellationToken ct = default);
}
