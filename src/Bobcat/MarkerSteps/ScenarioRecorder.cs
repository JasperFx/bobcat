using System.Diagnostics;
using Bobcat.Monitoring;

namespace Bobcat;

/// <summary>
/// The ambient scenario a marker-step test is inside (issue #110). Generated interceptors report
/// steps here, and the runner adapter opens and closes the scenario around each test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ambient because a generated interceptor has nowhere else to look.</b> It replaces a call in
/// a test body it did not write and cannot be handed a context. <c>AsyncLocal</c> is the idiom the
/// codebase already uses for exactly this — see <c>BobcatClock</c> — and it flows across the
/// awaits a test is full of.
/// </para>
/// <para>
/// <b>Silent when nothing opened a scenario.</b> A decorated helper is called from plenty of places
/// that are not specifications, and reporting from them would be noise at best. No scenario, no
/// recording, no throw.
/// </para>
/// </remarks>
public static class ScenarioRecorder
{
    private static readonly AsyncLocal<Recording?> _current = new();

    /// <summary>The scenario in progress on this async context, or null.</summary>
    public static Recording? Current => _current.Value;

    /// <summary>
    /// Open a scenario. Disposing the returned handle closes it and publishes the verdict.
    /// </summary>
    public static Recording Begin(string feature, string scenario, IMonitorEventSink? publisher, Guid runId)
    {
        var recording = new Recording(feature, scenario, publisher, runId);
        _current.Value = recording;
        return recording;
    }

    /// <summary>
    /// Record a step. Returns a handle whose disposal ends it — so an interceptor can wrap the
    /// call it replaced and the step's duration is the helper's real duration.
    /// </summary>
    public static IDisposable Step(string keyword, string text)
        => _current.Value?.BeginStep(keyword, text) ?? NoStep.Instance;

    /// <summary>A step with no keyword — a marker comment supplies its own.</summary>
    public static IDisposable Step(string text) => Step("", text);

    public sealed class Recording : IDisposable
    {
        private readonly List<RecordedStep> _steps = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly IMonitorEventSink? _publisher;
        private readonly Guid _runId;

        internal Recording(string feature, string scenario, IMonitorEventSink? publisher, Guid runId)
        {
            Feature = feature;
            Scenario = scenario;
            _publisher = publisher;
            _runId = runId;

            publisher?.Post(new ScenarioStarted(runId, Uid, feature, scenario, 1, DateTimeOffset.UtcNow));
        }

        public string Feature { get; }
        public string Scenario { get; }

        /// <summary>The identity that joins run evidence to a slice, with no mapping table.</summary>
        public string Uid => $"{Feature}/{Scenario}";

        public IReadOnlyList<RecordedStep> Steps => _steps;

        /// <summary>Set by the adapter when the test fails, so the verdict is the runner's.</summary>
        public Exception? Failure { get; set; }

        internal IDisposable BeginStep(string keyword, string text)
        {
            var step = new RecordedStep(keyword, text, _clock.ElapsedMilliseconds);
            _steps.Add(step);
            return new StepHandle(step, _clock);
        }

        public void Dispose()
        {
            _clock.Stop();
            _current.Value = null;

            _publisher?.Post(new ScenarioFinished(
                _runId, Uid,
                Failure is null ? "CleanPass" : "Failed",
                Attempts: 1,
                DurationMs: _clock.ElapsedMilliseconds,
                ErrorMessage: Failure?.Message,
                At: DateTimeOffset.UtcNow));
        }

        private sealed class StepHandle(RecordedStep step, Stopwatch clock) : IDisposable
        {
            public void Dispose() => step.EndedAtMs = clock.ElapsedMilliseconds;
        }
    }

    public sealed class RecordedStep(string keyword, string text, long startedAtMs)
    {
        public string Keyword { get; } = keyword;
        public string Text { get; } = text;
        public long StartedAtMs { get; } = startedAtMs;
        public long? EndedAtMs { get; set; }

        /// <summary>Null while the step is still running — which is what makes progress visible.</summary>
        public long? DurationMs => EndedAtMs - StartedAtMs;

        public override string ToString()
            => (Keyword.Length > 0 ? Keyword + " " : "") + Text;
    }

    private sealed class NoStep : IDisposable
    {
        public static readonly NoStep Instance = new();
        public void Dispose() { }
    }
}
