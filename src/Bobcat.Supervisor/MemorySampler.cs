namespace Bobcat.Supervisor;

/// <summary>
/// One sampled worker process's memory story (issue #149): where its resident set started,
/// the highest it was ever seen, and where it ended. All figures are real samples — a worker
/// that was never measurable produces no <see cref="WorkerMemory"/> at all, never zeroes.
/// </summary>
public sealed record WorkerMemory(WorkerLaunchContext Worker, long FirstBytes, long PeakBytes, long LastBytes)
{
    /// <summary>What the process grew over its life — the 375 MB → 9334 MB story, as one number.</summary>
    public long GrowthBytes => LastBytes - FirstBytes;
}

/// <summary>
/// What one attempt did to its process's resident set (issue #149): the delta from the
/// attempt's start to its verdict. One record per attempt, so a retried test appears once per
/// run of it.
/// </summary>
/// <param name="RetainedBytes">
/// The delta, when it can honestly be assigned to this attempt — negative when the process
/// shrank across it. Null when other tests were in flight in the same process during the
/// attempt: with overlap the subtraction has no single owner, and a wrong attribution is worse
/// than a declared gap.
/// </param>
public sealed record TestMemory(string Uid, string DisplayName, long? RetainedBytes, WorkerLaunchContext Worker);

/// <summary>
/// One extra sampling target's memory story (bobcat#702) — the database container beside the
/// workers. Unlike <see cref="WorkerMemory"/>, a target is reported even when it never produced
/// a sample, because somebody asked for it by name: every figure is then null, never zero, so
/// "docker was not reachable" reads differently from "the database used nothing".
/// </summary>
public sealed record TargetMemory(string Name, long? FirstBytes, long? PeakBytes, long? LastBytes)
{
    /// <summary>True once the target produced at least one sample.</summary>
    public bool IsMeasured => FirstBytes is not null;

    /// <summary>What the target grew over the run; null when unmeasured.</summary>
    public long? GrowthBytes => LastBytes - FirstBytes;
}

/// <summary>
/// Samples worker resident sets and attributes deltas per attempt — RunTiming for RSS
/// (issue #149). Boundary samples ride the same test-update stream the in-flight ledger reads
/// (start and verdict bracket the attempt); the run ticker adds periodic samples in between so
/// a peak that comes and goes mid-test is still seen. Everything is behind one lock: updates
/// arrive on worker I/O threads, periodic samples on the timer.
/// </summary>
internal sealed class MemorySampler
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<IWorkerClient, WorkerState> _workers = new(ReferenceEqualityComparer.Instance);
    private readonly List<TestMemory> _tests = [];
    private readonly List<(IMemorySampleTarget Target, Reading Reading)> _targets;
    private Task _targetPass = Task.CompletedTask;
    private bool _targetsStarted;
    private long _lastSample;

    /// <summary>First, peak and last of one sampled series.</summary>
    private sealed class Reading
    {
        public long? First;
        public long? Peak;
        public long? Last;

        public void Record(long? bytes)
        {
            if (bytes is not { } sampled) return;

            First ??= sampled;
            Last = sampled;
            if (Peak is not { } peak || sampled > peak) Peak = sampled;
        }
    }

    private sealed class WorkerState(WorkerLaunchContext launch)
    {
        public WorkerLaunchContext Launch { get; } = launch;
        public long? First;
        public long? Peak;
        public long? Last;

        /// <summary>Attempts currently running in THIS process — the attribution unit.</summary>
        public Dictionary<string, Attempt> InFlight { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Attempt(string displayName, long? startBytes, bool alone)
    {
        public string DisplayName { get; } = displayName;
        public long? StartBytes { get; } = startBytes;

        /// <summary>
        /// True only while this attempt has had its process to itself for its whole window.
        /// A second test entering the process poisons everyone — including itself.
        /// </summary>
        public bool Alone { get; set; } = alone;
    }

    public MemorySampler(TimeProvider time, IEnumerable<IMemorySampleTarget>? targets = null)
    {
        _time = time;
        _targets = (targets ?? []).Select(target => (target, new Reading())).ToList();
        _lastSample = time.GetTimestamp();
    }

    /// <summary>Registers a launched worker and takes its baseline sample.</summary>
    public void Track(IWorkerClient worker, WorkerLaunchContext launch)
    {
        var bytes = worker.SampleWorkingSet();
        lock (_gate)
        {
            var state = new WorkerState(launch);
            _workers[worker] = state;
            record(state, bytes);
        }
    }

    /// <summary>
    /// Folds one live test update in: an in-progress update opens the attempt's window with a
    /// fresh sample, a terminal one closes it and books the delta — or declines to, when the
    /// window was shared.
    /// </summary>
    public void Apply(IWorkerClient worker, WorkerTestUpdate update)
    {
        // Sampled outside the lock — it is a syscall against the client, not our state.
        var bytes = worker.SampleWorkingSet();

        lock (_gate)
        {
            if (!_workers.TryGetValue(worker, out var state)) return;
            record(state, bytes);

            if (update.InProgress)
            {
                var alone = state.InFlight.Count == 0;
                if (!alone)
                {
                    foreach (var other in state.InFlight.Values) other.Alone = false;
                }

                state.InFlight[update.Uid] = new Attempt(update.DisplayName, bytes, alone);
            }
            else if (state.InFlight.Remove(update.Uid, out var attempt))
            {
                // No samples means unmeasured — no record at all, never a zero. A shared
                // window with real samples is recorded with a null delta: the attempt ran and
                // was measured, but the subtraction has no single owner.
                if (attempt.StartBytes is not { } start || bytes is not { } end) return;

                _tests.Add(new TestMemory(
                    update.Uid, attempt.DisplayName,
                    attempt.Alone ? end - start : null,
                    state.Launch));
            }
        }
    }

    /// <summary>The run ticker's periodic pass: refresh every live worker's peak.</summary>
    public void SamplePeaks()
    {
        KeyValuePair<IWorkerClient, WorkerState>[] entries;
        lock (_gate) entries = _workers.ToArray();

        foreach (var (worker, state) in entries)
        {
            var bytes = worker.SampleWorkingSet();
            lock (_gate) record(state, bytes);
        }
    }

    /// <summary>
    /// Samples every extra target once (bobcat#702). Awaited for the run's first and last
    /// samples; the ticker uses <see cref="BeginTargetPass"/> instead. A target that throws or
    /// overruns is unmeasured for that pass — a sampler must never fail a run.
    /// </summary>
    public async Task SampleTargets(CancellationToken ct = default)
    {
        if (_targets.Count == 0) return;

        lock (_gate) _targetsStarted = true;

        var samples = await Task.WhenAll(_targets.Select(async entry =>
        {
            try
            {
                return await entry.Target.SampleBytes(ct);
            }
            catch
            {
                return null;
            }
        }));

        lock (_gate)
        {
            for (var i = 0; i < _targets.Count; i++) _targets[i].Reading.Record(samples[i]);
        }
    }

    /// <summary>
    /// The ticker's pass over the targets, off the timer thread: a target is usually a
    /// <c>docker</c> process taking a second or two, and the same tick checks for stalls. A
    /// pass still running when the next is due is not doubled up.
    /// </summary>
    public void BeginTargetPass()
    {
        if (_targets.Count == 0) return;

        lock (_gate)
        {
            if (!_targetPass.IsCompleted) return;
            _targetPass = Task.Run(() => SampleTargets());
        }
    }

    /// <summary>
    /// The run's last target sample, after any pass still in flight. Nothing at all when no
    /// pass ever started — a run that never got as far as testing samples nothing.
    /// </summary>
    public async Task FinishTargets()
    {
        Task pending;
        lock (_gate)
        {
            if (!_targetsStarted) return;
            pending = _targetPass;
        }

        await pending;
        await SampleTargets();
    }

    /// <summary>True once per interval — same consume-the-beat contract as the heartbeat's.</summary>
    public bool SampleDue(TimeSpan interval)
    {
        lock (_gate)
        {
            if (_time.GetElapsedTime(_lastSample) < interval) return false;
            _lastSample = _time.GetTimestamp();
            return true;
        }
    }

    private static void record(WorkerState state, long? bytes)
    {
        if (bytes is not { } sampled) return;

        state.First ??= sampled;
        state.Last = sampled;
        if (state.Peak is not { } peak || sampled > peak) state.Peak = sampled;
    }

    /// <summary>The highest resident set any worker has shown so far; null while nothing measured.</summary>
    public long? PeakBytes
    {
        get
        {
            lock (_gate)
            {
                long? peak = null;
                foreach (var state in _workers.Values)
                {
                    if (state.Peak is { } bytes && (peak is not { } current || bytes > current))
                    {
                        peak = bytes;
                    }
                }

                return peak;
            }
        }
    }

    /// <summary>Every worker that produced at least one sample.</summary>
    public IReadOnlyList<WorkerMemory> Workers
    {
        get
        {
            lock (_gate)
            {
                return _workers.Values
                    .Where(state => state.First is not null)
                    .Select(state => new WorkerMemory(
                        state.Launch, state.First!.Value, state.Peak!.Value, state.Last!.Value))
                    .ToList();
            }
        }
    }

    /// <summary>Every configured extra target, measured or not, in registration order.</summary>
    public IReadOnlyList<TargetMemory> Targets
    {
        get
        {
            lock (_gate)
            {
                return _targets
                    .Select(entry => new TargetMemory(
                        entry.Target.Name, entry.Reading.First, entry.Reading.Peak, entry.Reading.Last))
                    .ToList();
            }
        }
    }

    /// <summary>Every measured attempt, in completion order.</summary>
    public IReadOnlyList<TestMemory> Tests
    {
        get { lock (_gate) return _tests.ToList(); }
    }
}
