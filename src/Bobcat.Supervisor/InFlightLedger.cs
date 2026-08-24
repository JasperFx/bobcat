namespace Bobcat.Supervisor;

/// <summary>One test currently in flight, as the supervisor sees it.</summary>
/// <param name="InFlight">How long the test has been running, as of when this view was taken.</param>
public sealed record InFlightTest(string Uid, string DisplayName, WorkerLaunchContext Worker, TimeSpan InFlight);

/// <summary>
/// A test the supervisor reported as stalled — in flight longer than its threshold allowed
/// (issue #145). Detection is the whole feature: the name of the hung test is exactly what a
/// capped CI job cannot produce today.
/// </summary>
/// <param name="InFlight">How long the test had been in flight when the stall was detected.</param>
public sealed record StalledTest(string Uid, string DisplayName, TimeSpan InFlight, WorkerLaunchContext Worker);

/// <summary>
/// A progress line's worth of facts while a run is in flight (issue #148): how far along the
/// run is, what is running right now, and — the clause that matters — what has been running
/// longest. A reader spots a stuck run from a heartbeat whose longest-running figure keeps
/// climbing, well before any stall threshold fires.
/// </summary>
public sealed record SupervisorHeartbeat(
    TimeSpan Elapsed, int Completed, int Total, IReadOnlyList<InFlightTest> InFlight)
{
    public InFlightTest? LongestRunning
        => InFlight.Count == 0 ? null : InFlight.MaxBy(t => t.InFlight);

    /// <summary>The single log line. One line however many lanes are running.</summary>
    public string Describe()
    {
        var line = $"{Clock(Elapsed)} — {Completed}/{Total} done, {InFlight.Count} in flight";

        if (InFlight.Count > 0)
        {
            var lanes = InFlight.Select(t => t.Worker.Lane).Distinct().OrderBy(l => l).ToList();
            line += lanes.Count == 1
                ? $" (lane {lanes[0]})"
                : $" (lanes {string.Join(", ", lanes)})";

            var longest = LongestRunning!;
            line += $", longest running: {longest.DisplayName} ({(int)longest.InFlight.TotalSeconds}s)";
        }

        return line;
    }

    internal static string Clock(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}h{elapsed.Minutes:00}m"
        : elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s"
            : $"{(int)elapsed.TotalSeconds}s";
}

/// <summary>
/// The supervisor's live view of which tests are in flight right now — bookkeeping over the
/// <c>testing/testUpdates/tests</c> stream it already consumes (issues #145/#148, and the
/// attribution #149 and the snapshot #150 will read). Written from worker I/O threads, read
/// from a timer, so everything is behind one lock.
/// </summary>
internal sealed class InFlightLedger
{
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly Dictionary<string, Entry> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly List<StalledTest> _stalled = [];
    private readonly object _gate = new();
    private long _lastHeartbeat;

    private sealed class Entry(string displayName, WorkerLaunchContext worker, long startedAt)
    {
        public string DisplayName { get; } = displayName;
        public WorkerLaunchContext Worker { get; } = worker;
        public long StartedAt { get; } = startedAt;

        /// <summary>Once per attempt: a stall is announced when crossed, not on every tick.</summary>
        public bool StallReported { get; set; }
    }

    public InFlightLedger(TimeProvider time)
    {
        _time = time;
        _started = time.GetTimestamp();
        _lastHeartbeat = _started;
    }

    /// <summary>The post-filter test count, set once discovery has run. Zero until then.</summary>
    public int TotalTests { get; set; }

    /// <summary>
    /// Folds one live update in. An in-progress update opens (or, on a retry, reopens) the
    /// test's entry with a fresh start time — a new attempt is a new wait, so its stall clock
    /// and its once-per-attempt reporting both reset. Anything else closes the entry.
    /// </summary>
    public void Apply(WorkerLaunchContext worker, WorkerTestUpdate update)
    {
        lock (_gate)
        {
            if (update.InProgress)
            {
                _inFlight[update.Uid] = new Entry(update.DisplayName, worker, _time.GetTimestamp());
            }
            else
            {
                _inFlight.Remove(update.Uid);
                // A set, not a counter: a retried test is still one test done, and the
                // heartbeat's "done" figure must never exceed its total.
                _completed.Add(update.Uid);
            }
        }
    }

    /// <summary>
    /// Tests newly over their threshold since the last check. Each attempt is reported once —
    /// the heartbeat's climbing longest-running figure is the continuous view, this is the
    /// threshold crossing.
    /// </summary>
    /// <remarks>
    /// <paramref name="thresholdFor"/> is user code (<c>Supervisor.StallThresholdFor</c>), so it
    /// is evaluated outside the lock. The seam that opens — a test finishing while its threshold
    /// is being read — resolves in favour of reporting: it really was in flight past its budget.
    /// </remarks>
    public IReadOnlyList<StalledTest> DetectStalls(Func<string, TimeSpan?> thresholdFor)
    {
        List<(string Uid, Entry Entry)> candidates;
        lock (_gate)
        {
            candidates = _inFlight
                .Where(pair => !pair.Value.StallReported)
                .Select(pair => (pair.Key, pair.Value))
                .ToList();
        }

        if (candidates.Count == 0) return [];

        var found = new List<StalledTest>();
        foreach (var (uid, entry) in candidates)
        {
            if (thresholdFor(uid) is not { } threshold) continue;

            var inFlight = _time.GetElapsedTime(entry.StartedAt);
            if (inFlight < threshold) continue;

            lock (_gate)
            {
                if (entry.StallReported) continue;
                entry.StallReported = true;

                var stalled = new StalledTest(uid, entry.DisplayName, inFlight, entry.Worker);
                _stalled.Add(stalled);
                found.Add(stalled);
            }
        }

        return found;
    }

    /// <summary>
    /// True once per interval — and consumes the beat, so one timer tick produces at most one
    /// heartbeat however the tick cadence relates to the interval.
    /// </summary>
    public bool HeartbeatDue(TimeSpan interval)
    {
        lock (_gate)
        {
            if (_time.GetElapsedTime(_lastHeartbeat) < interval) return false;
            _lastHeartbeat = _time.GetTimestamp();
            return true;
        }
    }

    /// <summary>A consistent view of right now, safe to take from any thread.</summary>
    public SupervisorHeartbeat Snapshot()
    {
        lock (_gate)
        {
            var inFlight = _inFlight
                .Select(pair => new InFlightTest(
                    pair.Key, pair.Value.DisplayName, pair.Value.Worker,
                    _time.GetElapsedTime(pair.Value.StartedAt)))
                .ToList();

            return new SupervisorHeartbeat(
                _time.GetElapsedTime(_started), _completed.Count, TotalTests, inFlight);
        }
    }

    /// <summary>Every stall the run reported, in detection order.</summary>
    public IReadOnlyList<StalledTest> Stalled
    {
        get { lock (_gate) return _stalled.ToList(); }
    }
}
