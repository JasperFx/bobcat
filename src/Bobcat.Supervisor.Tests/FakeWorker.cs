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

    /// <summary>Every Run call this worker received, as the uid list asked for.</summary>
    public List<IReadOnlyList<string>?> Runs { get; } = [];

    public Task<IReadOnlyList<WorkerTest>> Discover(CancellationToken ct = default)
        => Task.FromResult(_factory.Tests);

    public Task<WorkerRunResult> Run(IReadOnlyList<string>? uids = null, CancellationToken ct = default)
    {
        Runs.Add(uids);

        var requested = uids ?? _factory.Tests.Select(t => t.Uid).ToList();
        var outcomes = new List<WorkerOutcome>();

        foreach (var uid in requested)
        {
            var attempt = _factory.RecordAttempt(uid);
            var state = _factory.StateFor(uid, attempt, this);

            if (state is null) continue; // withheld — models a worker that died before reporting

            var test = _factory.Tests.FirstOrDefault(t => t.Uid == uid);
            outcomes.Add(new WorkerOutcome(uid, test?.DisplayName ?? uid, state.Value)
            {
                Traits = test?.Traits ?? new Dictionary<string, string>(),
                ErrorType = state == WorkerTestState.Passed ? null : _factory.ErrorTypeFor(uid, attempt),
                ErrorMessage = state == WorkerTestState.Passed ? null : $"{uid} attempt {attempt}"
            });
        }

        var fault = _factory.FaultFor(this);

        return Task.FromResult(new WorkerRunResult(
            MtpWorkerClient.Complete(uids, outcomes, fault)) { Fault = fault });
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

    /// <summary>
    /// The exception type name a failing test reports. Null models a framework that erases it —
    /// tUnit does exactly that on the MTP wire.
    /// </summary>
    public Func<string, int, string?> ErrorType { get; init; } = (_, _) => null;

    public Task<IWorkerClient> Launch(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var worker = new FakeWorker(this) { Index = _launched.Count };
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
