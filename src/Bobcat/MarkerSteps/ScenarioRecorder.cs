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

            // What the test SAYS it does, read from its marker comments at compile time. Known
            // before a line of it runs, which is why the step count can be announced up front.
            Declared = DeclaredSteps.For(Uid);

            publisher?.Post(new ScenarioStarted(
                runId, Uid, feature, scenario, 1, DateTimeOffset.UtcNow,
                TotalSteps: Declared.Count > 0 ? Declared.Count : null));
        }

        public string Feature { get; }
        public string Scenario { get; }

        /// <summary>The identity that joins run evidence to a slice, with no mapping table.</summary>
        public string Uid => $"{Feature}/{Scenario}";

        /// <summary>The steps this scenario's marker comments declare, in source order.</summary>
        public IReadOnlyList<DeclaredStep> Declared { get; }

        public IReadOnlyList<RecordedStep> Steps => _steps;

        /// <summary>Set by the adapter when the test fails, so the verdict is the runner's.</summary>
        public Exception? Failure { get; set; }

        /// <summary>
        /// The failure as the runner described it, when there is no exception to hand over —
        /// xUnit v3 reports exception <i>types and messages</i> on <c>TestContext.TestState</c>,
        /// never the exception itself. Set by <see cref="MarkerStepRun.EndScenario"/>; either this
        /// or <see cref="Failure"/> makes the scenario a failure.
        /// </summary>
        public string? FailureDescription { get; set; }

        private bool _cancelled;

        internal IDisposable BeginStep(string keyword, string text)
        {
            var step = new RecordedStep(keyword, text, _clock.ElapsedMilliseconds)
            {
                StepId = "s" + (_steps.Count + 1)
            };
            _steps.Add(step);

            // Published as it opens, not at the end: a watcher showing a run in flight needs to
            // see the step that is currently taking the time, which is exactly the step that has
            // not finished yet.
            _publisher?.Post(new StepStarted(
                _runId, Uid, step.StepId, keyword, text,
                StepNumber: _steps.Count,
                TotalSteps: Declared.Count > 0 ? Declared.Count : null,
                ScenarioElapsedMs: step.StartedAtMs));

            return new StepHandle(this, step, _clock);
        }

        internal void EndStep(RecordedStep step, long endedAtMs, Exception? failure)
        {
            step.EndedAtMs = endedAtMs;
            step.Failure = failure;

            _publisher?.Post(new StepFinished(
                _runId, Uid, step.StepId,
                failure is null ? "Passed" : "Failed",
                DurationMs: endedAtMs - step.StartedAtMs,
                ErrorMessage: failure?.Message,
                ScenarioElapsedMs: endedAtMs));
        }

        /// <summary>
        /// Withdraw the scenario: close it locally and publish no verdict at all. For a test that
        /// was skipped or never ran — it asserted nothing, and every outcome word available here
        /// would claim it did.
        /// </summary>
        public void Cancel()
        {
            _cancelled = true;
            Dispose();
        }

        public void Dispose()
        {
            _clock.Stop();
            _current.Value = null;

            if (_cancelled) return;

            var failure = FailureDescription ?? Failure?.Message;

            _publisher?.Post(new ScenarioFinished(
                _runId, Uid,
                failure is null ? "CleanPass" : "Failed",
                Attempts: 1,
                DurationMs: _clock.ElapsedMilliseconds,
                ErrorMessage: failure,
                At: DateTimeOffset.UtcNow));
        }

        private sealed class StepHandle(Recording recording, RecordedStep step, Stopwatch clock) : IStepHandle
        {
            private bool _ended;

            public void Fail(Exception exception) => end(exception);

            public void Dispose() => end(null);

            private void end(Exception? failure)
            {
                // Track() fails the step and then its finally disposes it. First call wins, so the
                // failure is not overwritten by the disposal that follows it.
                if (_ended) return;
                _ended = true;

                recording.EndStep(step, clock.ElapsedMilliseconds, failure);
            }
        }
    }

    public sealed class RecordedStep(string keyword, string text, long startedAtMs)
    {
        public string Keyword { get; } = keyword;
        public string Text { get; } = text;
        public long StartedAtMs { get; } = startedAtMs;
        public long? EndedAtMs { get; set; }

        /// <summary>Unique within the scenario; the id the wire events key on.</summary>
        public string StepId { get; internal set; } = "";

        /// <summary>What the helper threw, when it threw. Null on a step that passed.</summary>
        public Exception? Failure { get; internal set; }

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

/// <summary>
/// A step in progress that can be told it failed. Separate from <see cref="IDisposable"/> so
/// <see cref="MarkerStepRuntime"/> can report the exception it already had to catch, without
/// every caller of <c>ScenarioRecorder.Step</c> having to know about it.
/// </summary>
public interface IStepHandle : IDisposable
{
    void Fail(Exception exception);
}
