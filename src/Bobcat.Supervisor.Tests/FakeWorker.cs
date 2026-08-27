namespace Bobcat.Supervisor.Tests;

/// <summary>
/// A scripted worker. Lets the scheduling and policy logic be tested exhaustively and instantly,
/// without paying ~100ms per process — the real protocol is covered separately by
/// <see cref="SupervisorEndToEndTests"/>.
/// </summary>
public sealed class FakeWorker : IWorkerClient
{
    private readonly FakeWorkerFactory _factory;

    public FakeWorker(FakeWorkerFactory factory) => _factory = factory;

    public int Index { get; init; }
    public bool Disposed { get; private set; }

    private readonly TaskCompletionSource _killed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The reason the supervisor killed this worker, when it did (issue #173).</summary>
    public string? KilledReason { get; private set; }

    /// <summary>
    /// Models the process-tree kill: an in-flight <see cref="Run"/> stops where it stands and
    /// returns a fault, with indeterminate outcomes synthesized for everything unreported —
    /// exactly how <see cref="MtpWorkerClient"/> experiences its process dying.
    /// </summary>
    public ValueTask Kill(string reason)
    {
        KilledReason = reason;
        _killed.TrySetResult();
        return default;
    }

    /// <summary>Scripted pid — null by default, modelling an in-process client (issue #146).</summary>
    public int? ProcessId { get; init; }

    /// <summary>Scripted resident set — null by default, modelling a client that cannot measure (issue #149).</summary>
    public long? SampleWorkingSet() => _factory.SampleWorkingSet(this);

    /// <summary>What the supervisor said it was launching this worker for.</summary>
    public WorkerLaunchContext Launch { get; init; } = WorkerLaunchContext.Discovery;

    /// <summary>Every Run call this worker received, as the uid list asked for.</summary>
    public List<IReadOnlyList<string>?> Runs { get; } = [];

    private readonly List<Action<WorkerTestUpdate>> _testUpdateHandlers = [];

    /// <summary>
    /// Models the live MTP stream: a real host reports each test in progress, then its verdict.
    /// </summary>
    public void OnTestUpdate(Action<WorkerTestUpdate> handler) => _testUpdateHandlers.Add(handler);

    private void relay(WorkerTestUpdate update)
    {
        foreach (var handler in _testUpdateHandlers) handler(update);
    }

    public Task<IReadOnlyList<WorkerTest>> Discover(CancellationToken ct = default)
        => Task.FromResult(_factory.Tests);

    public async Task<WorkerRunResult> Run(IReadOnlyList<string>? uids = null, CancellationToken ct = default)
    {
        Runs.Add(uids);

        var requested = uids ?? _factory.Tests.Select(t => t.Uid).ToList();
        var outcomes = new List<WorkerOutcome>();

        foreach (var uid in requested)
        {
            // A dead process runs nothing further.
            if (_killed.Task.IsCompleted) break;

            var test = _factory.Tests.FirstOrDefault(t => t.Uid == uid);
            var displayName = _factory.ReportedNameFor(uid) ?? test?.DisplayName ?? uid;
            var traits = test?.Traits ?? new Dictionary<string, string>();

            relay(new WorkerTestUpdate(uid, displayName, "in-progress") { Traits = traits });

            // After the in-progress report, before any verdict — a hung test, as the
            // supervisor experiences one. A kill breaks the hold the way it would break a
            // real wedged process.
            if (_factory.HoldAfterStart is { } hold) await Task.WhenAny(hold(uid, this), _killed.Task);
            if (_killed.Task.IsCompleted) break;

            var attempt = _factory.RecordAttempt(uid);
            var state = _factory.StateFor(uid, attempt, this);

            if (state is null) continue; // withheld — models a worker that died before reporting

            outcomes.Add(new WorkerOutcome(uid, displayName, state.Value)
            {
                Traits = traits,
                ErrorType = state == WorkerTestState.Passed ? null : _factory.ErrorTypeFor(uid, attempt),
                ErrorMessage = state == WorkerTestState.Passed ? null : $"{uid} attempt {attempt}",
                Duration = _factory.DurationFor(uid, attempt)
            });

            relay(new WorkerTestUpdate(uid, displayName, state.Value.ToString().ToLowerInvariant())
            {
                State = state,
                Traits = traits
            });
        }

        var fault = _factory.FaultFor(this)
                    ?? (KilledReason is null ? null : $"the worker process was killed: {KilledReason}");

        return new WorkerRunResult(
            MtpWorkerClient.Complete(uids, outcomes, fault))
        {
            Fault = fault,
            ExitCode = fault is null ? null : _factory.FaultExitCode,
            StandardError = fault is null ? null : _factory.FaultStandardError,
            ProcessId = ProcessId
        };
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return default;
    }
}

/// <summary>Builds <see cref="FakeWorker"/>s and scripts what they report.</summary>
public sealed class FakeWorkerFactory : IWorkerFactory
{
    // Locked throughout: with MaxParallelWorkers > 1 several lanes call into this factory at the
    // same moment, and an unsynchronised counter would make the parallel tests flaky — which is a
    // uniquely bad property for the tests that exist to prove parallelism is safe.
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly List<FakeWorker> _launched = [];
    private readonly object _gate = new();

    public string Description => "fake";

    public IReadOnlyList<FakeWorker> Launched
    {
        get { lock (_gate) return _launched.ToList(); }
    }

    public required IReadOnlyList<WorkerTest> Tests { get; init; }

    /// <summary>Decides an outcome. Return null to report nothing at all for that test.</summary>
    public required Func<string, int, FakeWorker, WorkerTestState?> Outcome { get; init; }

    /// <summary>Optional per-worker fault, modelling a crashed process.</summary>
    public Func<FakeWorker, string?> Fault { get; init; } = _ => null;

    /// <summary>The exit code and standard error tail a faulting worker reports alongside <see cref="Fault"/>.</summary>
    public int? FaultExitCode { get; init; }

    public string? FaultStandardError { get; init; }

    /// <summary>
    /// The exception type name a failing test reports. Null models a framework that erases it —
    /// tUnit does exactly that on the MTP wire.
    /// </summary>
    public Func<string, int, string?> ErrorType { get; init; } = (_, _) => null;

    /// <summary>
    /// How long a test took. Null models a framework that reports no duration at all — tUnit
    /// erases it on the MTP wire the same way it erases exception types.
    /// </summary>
    public Func<string, int, TimeSpan?> Duration { get; init; } = (_, _) => null;

    /// <summary>
    /// The name a run-time result carries, when it differs from the discovered one. MTP node
    /// updates are free to disagree with discovery — a theory case resolved at run time, say.
    /// Null keeps the discovered name.
    /// </summary>
    public Func<string, string?> ReportedName { get; init; } = _ => null;

    /// <summary>
    /// The pid each launched worker reports, keyed by launch index. Defaults to null — an
    /// in-process client, which is exactly what a fake is.
    /// </summary>
    public Func<int, int?> ProcessIdFor { get; init; } = _ => null;

    /// <summary>
    /// Awaited between a test's in-progress report and its verdict — a hung test, held until
    /// the test case decides to let it finish. Null holds nothing.
    /// </summary>
    public Func<string, FakeWorker, Task>? HoldAfterStart { get; init; }

    /// <summary>
    /// The resident set a worker reports when sampled. Defaults to null — a client that cannot
    /// measure, which is what unmeasured-never-zero exists for. Every call is counted in
    /// <see cref="SamplesTaken"/> so a test can assert "off means not one sample".
    /// </summary>
    public Func<FakeWorker, long?> WorkingSet { get; init; } = _ => null;

    private int _samplesTaken;

    /// <summary>How many times any worker's working set was sampled.</summary>
    public int SamplesTaken => Volatile.Read(ref _samplesTaken);

    internal long? SampleWorkingSet(FakeWorker worker)
    {
        Interlocked.Increment(ref _samplesTaken);
        return WorkingSet(worker);
    }

    public Task<IWorkerClient> Launch(WorkerLaunchContext context, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var worker = new FakeWorker(this)
            {
                Index = _launched.Count,
                Launch = context,
                ProcessId = ProcessIdFor(_launched.Count)
            };
            _launched.Add(worker);
            return Task.FromResult<IWorkerClient>(worker);
        }
    }

    internal int RecordAttempt(string uid)
    {
        lock (_gate)
        {
            _attempts.TryGetValue(uid, out var count);
            _attempts[uid] = ++count;
            return count;
        }
    }

    internal WorkerTestState? StateFor(string uid, int attempt, FakeWorker worker)
        => Outcome(uid, attempt, worker);

    internal string? FaultFor(FakeWorker worker) => Fault(worker);

    internal string? ErrorTypeFor(string uid, int attempt) => ErrorType(uid, attempt);

    internal string? ReportedNameFor(string uid) => ReportedName(uid);

    internal TimeSpan? DurationFor(string uid, int attempt) => Duration(uid, attempt);

    /// <summary>Workers that actually ran something (the first is always discovery).</summary>
    public IReadOnlyList<FakeWorker> RunningWorkers => Launched.Where(w => w.Runs.Count > 0).ToList();

    /// <summary>A test whose display name places it in <paramref name="className"/>.</summary>
    public static WorkerTest InClass(string className, string method, params string[] traits)
        => Test($"{className}.{method}", traits);

    public static WorkerTest Test(string uid, params string[] traits)
        => new(uid, uid)
        {
            Traits = traits
                .Select(t => t.Split('='))
                .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "true", StringComparer.OrdinalIgnoreCase)
        };
}
