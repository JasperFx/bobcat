namespace Bobcat.Supervisor;

/// <summary>What told the supervisor a run had degraded (bobcat#824).</summary>
public enum DegradationCause
{
    /// <summary>
    /// The last <see cref="Supervisor.DegradationWindow"/> completed tests with a known duration
    /// each ran more than <see cref="Supervisor.DegradationFactor"/> times it.
    /// </summary>
    SlowTests,

    /// <summary>A test crossed its stall threshold (issue #145).</summary>
    Stall
}

/// <summary>
/// How fast tests are running against how fast they are known to run: the mean observed and
/// mean known duration over <see cref="Samples"/> tests, so <see cref="Factor"/> is the one
/// number a reader compares — 12.4x before a heal, 1.1x after it.
/// </summary>
public sealed record Pace(TimeSpan Observed, TimeSpan Known, int Samples)
{
    public double Factor => Known > TimeSpan.Zero ? Observed / Known : 0;

    public override string ToString()
        => $"{RunTiming.Humanize(Observed)} against {RunTiming.Humanize(Known)} known " +
           $"({Factor.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}x over {Samples} test(s))";
}

/// <summary>One completed test that ran past the degradation factor.</summary>
public sealed record SlowTest(string Uid, string DisplayName, TimeSpan Observed, TimeSpan Known);

/// <summary>
/// The run degraded (bobcat#824): it is slow, not stuck. The supervisor reports it through
/// <see cref="ISupervisorObserver.Degraded"/> and, when a <see cref="Supervisor.OnDegraded"/>
/// handler is set, pauses the run and asks the handler to heal.
/// </summary>
/// <param name="Reason">The sentence a log, an abort and a <c>track_healed</c> event carry.</param>
/// <param name="Pace">
/// The pace of the tests that tripped the signal — the window, for <see cref="DegradationCause.SlowTests"/>;
/// the stalled test against its known duration for <see cref="DegradationCause.Stall"/>, or null
/// when that test has none.
/// </param>
/// <param name="SlowTests">The tests the signal was read off, in completion order.</param>
public sealed record RunDegradation(
    DegradationCause Cause,
    string Reason,
    Pace? Pace,
    IReadOnlyList<SlowTest> SlowTests,
    DateTimeOffset At);

/// <summary>A track a <see cref="Supervisor.OnDegraded"/> handler healed, and which heal of it this was.</summary>
public sealed record HealedTrack(string Track, int Number);

/// <summary>
/// What a <see cref="Supervisor.OnDegraded"/> handler answers: the tracks it healed, or why the
/// run must stop instead. Healing nothing and aborting nothing is allowed — the run resumes on
/// fresh workers and nothing is recorded as a heal.
/// </summary>
public sealed class DegradationResponse
{
    private DegradationResponse(IReadOnlyList<HealedTrack> healed, string? abortReason)
    {
        Healed = healed;
        AbortReason = abortReason;
    }

    /// <summary>The tracks healed, in the order they were healed.</summary>
    public IReadOnlyList<HealedTrack> Healed { get; }

    /// <summary>Why the run is to abort instead of resuming — a heal budget spent, a heal that failed.</summary>
    public string? AbortReason { get; }

    public static DegradationResponse HealedTracks(params IReadOnlyList<HealedTrack> tracks) => new(tracks, null);

    public static DegradationResponse Abort(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("An abort needs a reason.", nameof(reason));
        return new DegradationResponse([], reason);
    }
}

/// <summary>
/// One heal of one track during a run (bobcat#824): what degraded, the pace before, and — once
/// the next <see cref="Supervisor.DegradationWindow"/> tests with a known duration have finished,
/// or the run has ended — the pace after, which is the evidence the heal worked.
/// </summary>
/// <param name="Number">Which heal of this track this is, counted by whoever owns the track (the gate counts per gate run).</param>
/// <param name="PaceBefore">The degraded pace; null when the degradation was a stall on a test with no known duration.</param>
public sealed record TrackHeal(
    string Track,
    int Number,
    DegradationCause Cause,
    string Reason,
    Pace? PaceBefore,
    DateTimeOffset At)
{
    /// <summary>
    /// The pace of the tests that finished after the heal, over however many had finished when
    /// it was measured — the window, or fewer if the run ended first. Null when none had.
    /// </summary>
    public Pace? PaceAfter { get; init; }

    public override string ToString()
    {
        var before = PaceBefore is { } b ? $"; before: {b}" : "";
        var after = PaceAfter is { } a ? $"; after: {a}" : "; after: not measured";
        return $"{Track} (heal {Number}) — {Reason}{before}{after}";
    }
}

/// <summary>
/// Reads the degradation signal off completed tests (bobcat#824): a test is slow when it ran more
/// than <c>factor</c> times its known duration; the run is degraded when the last <c>window</c>
/// tests with a known duration were all slow. Tests with no known duration are not counted, so a
/// first run — which has no durations — can never degrade. Written from worker I/O threads
/// through the supervisor's <c>OnTestUpdate</c> callback, so everything is behind one lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>The floor.</b> A 5ms test taking 60ms is 12x its known duration and tells nobody anything —
/// jitter on a fast suite would read as a degraded service. A test shorter than the floor is
/// never slow, whatever its known duration; the service degradations this exists for (bobcat#824:
/// 2s tests taking 2 minutes) clear a one-second floor by two orders of magnitude.
/// </para>
/// <para>
/// <b>The window clears when it fires</b>, so a second signal needs <c>window</c> fresh slow tests
/// — one signal per window while the run stays degraded, not one per test. After a heal the
/// detector also measures the pace of the next <c>window</c> tests, which is what a
/// <see cref="TrackHeal.PaceAfter"/> is.
/// </para>
/// </remarks>
internal sealed class DegradationDetector
{
    private readonly int _window;
    private readonly double _factor;
    private readonly TimeSpan _floor;
    private readonly IReadOnlyDictionary<string, TimeSpan> _known;
    private readonly Queue<SlowTest> _recent = new();
    private readonly List<SlowTest> _after = [];
    private readonly object _gate = new();
    private bool _measuringAfter;
    private bool _suspended;

    public DegradationDetector(int window, double factor, TimeSpan floor, IReadOnlyDictionary<string, TimeSpan> knownByUid)
    {
        _window = Math.Max(1, window);
        _factor = factor;
        _floor = floor;
        _known = knownByUid;
    }

    /// <summary>The window, floored at one.</summary>
    public int Window => _window;

    /// <summary>
    /// Stop reading the signal while a heal is pending or in progress: the tests finishing now ran
    /// against the degraded service, and counting them would trip a second signal before the first
    /// was acted on. <see cref="Resume"/> re-arms and starts the after-heal measurement.
    /// </summary>
    public void Suspend()
    {
        lock (_gate)
        {
            _suspended = true;
            _recent.Clear();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _suspended = false;
            _recent.Clear();
            _after.Clear();
            _measuringAfter = true;
        }
    }

    /// <summary>
    /// Fold one completed test in. Returns the degradation when this test completes a window of
    /// slow ones, and the after-heal pace when this test completes the window after a heal.
    /// </summary>
    public (RunDegradation? Degraded, Pace? AfterPace) Observe(string uid, string displayName, TimeSpan observed, DateTimeOffset at)
    {
        if (!_known.TryGetValue(uid, out var known) || known <= TimeSpan.Zero) return (null, null);

        var sample = new SlowTest(uid, displayName, observed, known);

        lock (_gate)
        {
            Pace? after = null;
            if (_measuringAfter)
            {
                _after.Add(sample);
                if (_after.Count >= _window)
                {
                    after = paceOf(_after);
                    _measuringAfter = false;
                }
            }

            if (_suspended) return (null, after);

            _recent.Enqueue(sample);
            while (_recent.Count > _window) _recent.Dequeue();

            if (_recent.Count < _window || !_recent.All(isSlow)) return (null, after);

            var slow = _recent.ToList();
            _recent.Clear();

            var pace = paceOf(slow);
            var reason = $"the last {slow.Count} completed test(s) with a known duration each ran more than " +
                         $"{_factor.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}x it: {pace} — " +
                         string.Join(", ", slow.Select(s => $"{s.DisplayName} ({RunTiming.Humanize(s.Observed)} vs {RunTiming.Humanize(s.Known)})"));

            return (new RunDegradation(DegradationCause.SlowTests, reason, pace, slow, at), after);
        }
    }

    /// <summary>
    /// The after-heal pace over whatever has finished so far — for a run that ends before the
    /// window fills. Null when nothing with a known duration finished after the heal.
    /// </summary>
    public Pace? PartialAfterPace()
    {
        lock (_gate)
        {
            if (!_measuringAfter || _after.Count == 0) return null;
            _measuringAfter = false;
            return paceOf(_after);
        }
    }

    /// <summary>The pace of one in-flight test against its known duration, for a stall-caused degradation.</summary>
    public Pace? PaceOf(string uid, TimeSpan inFlight)
        => _known.TryGetValue(uid, out var known) && known > TimeSpan.Zero ? new Pace(inFlight, known, 1) : null;

    private bool isSlow(SlowTest test) => test.Observed >= _floor && test.Observed > test.Known * _factor;

    private static Pace paceOf(IReadOnlyList<SlowTest> tests)
        => new(
            TimeSpan.FromTicks(tests.Sum(t => t.Observed.Ticks) / tests.Count),
            TimeSpan.FromTicks(tests.Sum(t => t.Known.Ticks) / tests.Count),
            tests.Count);
}
