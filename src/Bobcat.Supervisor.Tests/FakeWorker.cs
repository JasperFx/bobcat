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
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public string Description => "fake";

    public List<FakeWorker> Launched { get; } = [];

    public required IReadOnlyList<WorkerTest> Tests { get; init; }

    /// <summary>Decides an outcome. Return null to report nothing at all for that test.</summary>
    public required Func<string, int, FakeWorker, WorkerTestState?> Outcome { get; init; }

    /// <summary>Optional per-worker fault, modelling a crashed process.</summary>
    public Func<FakeWorker, string?> Fault { get; init; } = _ => null;

    public Task<IWorkerClient> Launch(CancellationToken ct = default)
    {
        var worker = new FakeWorker(this) { Index = Launched.Count };
        Launched.Add(worker);
        return Task.FromResult<IWorkerClient>(worker);
    }

    internal int RecordAttempt(string uid)
    {
        _attempts.TryGetValue(uid, out var count);
        _attempts[uid] = ++count;
        return count;
    }

    internal WorkerTestState? StateFor(string uid, int attempt, FakeWorker worker)
        => Outcome(uid, attempt, worker);

    internal string? FaultFor(FakeWorker worker) => Fault(worker);

    /// <summary>Workers that actually ran something (the first is always discovery).</summary>
    public IReadOnlyList<FakeWorker> RunningWorkers => Launched.Where(w => w.Runs.Count > 0).ToList();

    public static WorkerTest Test(string uid, params string[] traits)
        => new(uid, uid)
        {
            Traits = traits
                .Select(t => t.Split('='))
                .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "true", StringComparer.OrdinalIgnoreCase)
        };
}
