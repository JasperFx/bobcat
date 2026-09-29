using System.Diagnostics;
using Bobcat.Engine;
using Bobcat.Engine.Verification;
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

    /// <summary>
    /// Raised as each scenario closes, after its verdict is known — the seam a local renderer or a
    /// second reporter attaches to. Never raised for a withdrawn (skipped) scenario, which made no
    /// claim to report.
    /// </summary>
    /// <remarks>
    /// Public because the interesting consumers are outside this assembly: an adapter package, a
    /// consumer's own reporter, and <see cref="ProjectedSpecConsole"/>, which is only the first of
    /// them and gets no privileged access.
    /// </remarks>
    public static event Action<Recording>? ScenarioCompleted;

    /// <summary>The scenario in progress on this async context, or null.</summary>
    public static Recording? Current => _current.Value;

    /// <summary>
    /// The step in progress — the innermost <c>[BobcatStep]</c> helper currently executing — or
    /// null when no step is open.
    /// </summary>
    /// <remarks>
    /// This is what <see cref="SpecAssert"/> hangs a cell on, and the reason cells are a
    /// grammar-helper feature rather than a marker-comment one: a comment declares a step, it does
    /// not execute one, so there is no step object for a comment's narrative to carry a comparison.
    /// </remarks>
    public static RecordedStep? CurrentStep => _current.Value?.OpenStep;

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
    public static IDisposable Step(string keyword, string text) => Step(keyword, text, -1);

    /// <summary>
    /// Record a step that ran inside the <paramref name="declaredIndex"/>'th marker comment of
    /// the test (issue #304), 0-based, or <c>-1</c> for a call outside every declared region.
    /// </summary>
    /// <remarks>
    /// The index is decided by the generator from the call site's line against the comments in
    /// the same method — at compile time, where both facts are exact. Nothing here walks a stack
    /// or guesses from timing: <b>declared is not executed</b> survives only because the join
    /// between the two is a compile-time fact rather than a runtime inference.
    /// </remarks>
    public static IDisposable Step(string keyword, string text, int declaredIndex)
        => _current.Value?.BeginStep(keyword, text, declaredIndex) ?? NoStep.Instance;

    /// <summary>
    /// Record a step whose text is a <c>[BobcatStep]</c> template, rendered against the values
    /// the helper was actually called with (issue #339).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generated interceptor substitutes what it can see at compile time — a literal
    /// argument — and hands the rest here, because a type, a minted id or a constructed command
    /// object has no rendering until it exists. That is the whole of a typed store vocabulary, and
    /// without this its steps reached the canvas as <c>{event} is emitted</c>.
    /// </para>
    /// <para>
    /// Rendered even when no scenario is open is deliberately NOT done: with nothing recording
    /// there is nothing to render for, and a decorated helper is called from plenty of places
    /// that are not specifications.
    /// </para>
    /// </remarks>
    public static IDisposable Step(
        string keyword, string text, int declaredIndex, IReadOnlyList<StepArgument> arguments)
        => Step(keyword, text, declaredIndex, -1, arguments);

    /// <summary>
    /// Record a step that is the <paramref name="plannedIndex"/>'th <c>[BobcatStep]</c> call in its
    /// test method, 0-based, or <c>-1</c> for a call from somewhere that has no plan.
    /// </summary>
    /// <remarks>
    /// The ordinal is what makes "which planned steps never ran" a set lookup rather than a guess —
    /// see <see cref="PlannedSteps"/>. It survives a helper called twice, in a loop, or behind an
    /// <c>if</c>, because it describes the source rather than the execution.
    /// </remarks>
    public static IDisposable Step(
        string keyword, string text, int declaredIndex, int plannedIndex,
        IReadOnlyList<StepArgument> arguments)
    {
        var recording = _current.Value;
        if (recording is null) return NoStep.Instance;

        var rendered = StepText.RenderWithValues(text, arguments);
        return recording.BeginStep(keyword, rendered.Text, declaredIndex, plannedIndex, rendered.Values);
    }

    /// <summary>A step with no arguments to render, identified by its position in the plan.</summary>
    public static IDisposable Step(string keyword, string text, int declaredIndex, int plannedIndex)
        => _current.Value?.BeginStep(keyword, text, declaredIndex, plannedIndex, []) ?? NoStep.Instance;

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

            Planned = PlannedSteps.For(Uid);

            publisher?.Post(new ScenarioStarted(
                runId, Uid, feature, scenario, 1, DateTimeOffset.UtcNow,
                TotalSteps: Declared.Count > 0 ? Declared.Count : Planned.Count > 0 ? Planned.Count : null,
                // The narrative travels with the announcement rather than as steps: a declared
                // step has not run, and StepStarted is the event that says something did.
                DeclaredSteps: Declared.Count > 0
                    ? Declared.Select(x => new DeclaredStepInfo(x.Keyword, x.Text)).ToList()
                    : null,
                // The plan, for the same reason: a watcher can only grey the steps a scenario never
                // reached if it was told what the scenario meant to do.
                PlannedSteps: Planned.Count > 0
                    ? Planned.Select(x => new PlannedStepInfo(
                        x.Keyword, x.Template, x.Grammar, x.DeclaredStepNumber)).ToList()
                    : null));
        }

        public string Feature { get; }
        public string Scenario { get; }

        /// <summary>The identity that joins run evidence to a slice, with no mapping table.</summary>
        public string Uid => $"{Feature}/{Scenario}";

        /// <summary>The steps this scenario's marker comments declare, in source order.</summary>
        public IReadOnlyList<DeclaredStep> Declared { get; }

        /// <summary>The grammar steps this scenario plans to call, in source order.</summary>
        public IReadOnlyList<PlannedStep> Planned { get; } = [];

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

        /// <summary>The innermost step currently executing, or null between steps.</summary>
        public RecordedStep? OpenStep { get; private set; }

        private readonly List<Exception> _gatheredAssertions = new();

        /// <summary>
        /// Hold onto an assertion failure so the rest of its run still gets to be evaluated
        /// (<see cref="AssertionRun"/>).
        /// </summary>
        internal void GatherAssertionFailure(Exception failure)
        {
            lock (_gatheredAssertions) _gatheredAssertions.Add(failure);
        }

        /// <summary>
        /// Throw what the run gathered, and forget it. Called at the run's last assertion — the point
        /// just before the next action, which would otherwise operate on state the assertions have
        /// already shown to be wrong.
        /// </summary>
        internal void FlushAssertionRun()
        {
            List<Exception> gathered;
            lock (_gatheredAssertions)
            {
                if (_gatheredAssertions.Count == 0) return;

                gathered = _gatheredAssertions.ToList();
                _gatheredAssertions.Clear();
            }

            AssertionRun.Throw(gathered);
        }

        /// <summary>
        /// The last keyword that actually opened a block — Given, When or Then, never And or But.
        /// A repeat of it renders as <c>And</c>, which is how Gherkin has always been written.
        /// </summary>
        private string? _openKeyword;

        internal IDisposable BeginStep(string keyword, string text) => BeginStep(keyword, text, -1);

        internal IDisposable BeginStep(string keyword, string text, int declaredIndex)
            => BeginStep(keyword, text, declaredIndex, -1, []);

        /// <summary>A keyword that continues the block it is in rather than opening one.</summary>
        private static bool isContinuation(string keyword)
            => string.Equals(keyword, "And", StringComparison.OrdinalIgnoreCase)
               || string.Equals(keyword, "But", StringComparison.OrdinalIgnoreCase);

        internal IDisposable BeginStep(
            string keyword, string text, int declaredIndex, int plannedIndex,
            IReadOnlyList<StepTextSpan> valueSpans)
        {
            // An index the generator computed against a DIFFERENT set of comments than the one
            // registered here — a stale obj/ from before a comment was deleted, or a helper whose
            // enclosing class is not marked — attributes to nothing rather than to the wrong
            // sentence. The guard is cheap and the alternative is a confident lie.
            var declaredNumber = declaredIndex >= 0 && declaredIndex < Declared.Count
                ? declaredIndex + 1
                : (int?)null;

            // Gherkin's own convention: a step that repeats the previous step's keyword is written
            // `And`. A [BobcatStep] helper cannot do that for itself — its keyword is fixed on the
            // attribute, and whether a call is the first of its block or the third is a fact about
            // the scenario, not about the helper. So it is settled here, where the previous step is
            // known. A helper that hardcodes `And` to fit its usual position then stops being
            // necessary, and stops being WRONG in the position it did not expect: CritterCrush has
            // seven scenarios that open with `And` because their first step happens to come from a
            // helper written for the second. `And` and `But` pass through and do not close the block
            // they sit in, so Given / And / Given still reads Given / And / And.
            var rendered = keyword;
            if (keyword.Length == 0)
            {
                // A grammar that spells NO keyword — Storyteller and Gauge sentences read this way —
                // has none to promote and opens no block. Treating the empty string as a keyword
                // turned the second such step into `And`, which is a word the author never wrote.
                rendered = "";
            }
            else if (isContinuation(keyword))
            {
                rendered = keyword;
            }
            else if (string.Equals(keyword, _openKeyword, StringComparison.OrdinalIgnoreCase))
            {
                rendered = "And";
            }
            else
            {
                _openKeyword = keyword;
            }

            var planned = PlannedSteps.For(Uid);
            var plannedNumber = plannedIndex >= 0 && plannedIndex < planned.Count
                ? plannedIndex + 1
                : (int?)null;

            var step = new RecordedStep(rendered, text, _clock.ElapsedMilliseconds)
            {
                StepId = "s" + (_steps.Count + 1),
                DeclaredStepNumber = declaredNumber,
                PlannedStepNumber = plannedNumber,
                ValueSpans = valueSpans
            };
            _steps.Add(step);

            // Published as it opens, not at the end: a watcher showing a run in flight needs to
            // see the step that is currently taking the time, which is exactly the step that has
            // not finished yet.
            _publisher?.Post(new StepStarted(
                _runId, Uid, step.StepId, rendered, text,
                StepNumber: _steps.Count,
                TotalSteps: Declared.Count > 0 ? Declared.Count : Planned.Count > 0 ? Planned.Count : null,
                ScenarioElapsedMs: step.StartedAtMs,
                DeclaredStepNumber: declaredNumber,
                PlannedStepNumber: plannedNumber,
                Values: valueSpans.Count > 0
                    ? valueSpans.Select(x => new StepValueSpan(x.Start, x.Length)).ToList()
                    : null));

            var previous = OpenStep;
            if (previous is not null)
            {
                step.Depth = previous.Depth + 1;
                previous.Children.Add(step);
            }

            OpenStep = step;

            return new StepHandle(this, step, _clock, previous);
        }

        internal void EndStep(RecordedStep step, long endedAtMs, Exception? failure)
        {
            step.EndedAtMs = endedAtMs;

            // Never clear a failure the step already carries. SpecAssert.Fail records a wrong
            // WITHOUT throwing, so the disposal that follows arrives with a null failure — and
            // assigning it unconditionally would erase exactly the failures that were gathered
            // rather than thrown.
            if (failure is not null) step.Failure ??= failure;

            // A real throw only. A gathered wrong carries a SpecAssertionException that was never
            // thrown, so it has no stack, and dressing it up as one would make a clean failure
            // message look like a crash.
            var thrown = step.Failure is not null && !ProjectedFailure.IsAssertion(step.Failure)
                ? step.Failure
                : null;

            var filtered = SpecStackTrace.Filter(thrown);

            _publisher?.Post(new StepFinished(
                _runId, Uid, step.StepId,
                step.Status == ResultStatus.success ? "Passed" : "Failed",
                DurationMs: endedAtMs - step.StartedAtMs,
                ErrorMessage: describe(step),
                ScenarioElapsedMs: endedAtMs,
                Cells: step.Cells.Count > 0
                    ? step.Cells.Select(toWire).ToList()
                    : null,
                ExceptionType: thrown?.GetType().Name,
                StackTrace: thrown?.StackTrace,
                StackFrames: filtered.Frames.Count > 0 ? filtered.Frames : null,
                HiddenStackFrames: filtered.Hidden));
        }

        /// <summary>A cell as the wire carries it — the framework's own status word, verbatim.</summary>
        private static StepCell toWire(CellResult cell)
            => new(cell.Name, cell.Status.ToString(), cell.Expected, cell.Actual, cell.Note, cell.RowIndex);

        /// <summary>
        /// The one-line failure for a step: the exception's message, else the first failed cell's
        /// expected/actual pair. Null for a step with nothing wrong.
        /// </summary>
        internal static string? describe(RecordedStep step)
        {
            // An EMPTY message is "nothing to say", not "say nothing". A bare fact fails with no
            // message at all, and every reader of this has to render the step's red without printing
            // a blank line under it.
            if (step.Failure is not null)
                return step.Failure.Message.Length > 0 ? step.Failure.Message : null;

            var cell = step.Cells.FirstOrDefault(x => x.Status is not (ResultStatus.success or ResultStatus.ok));
            return cell is null ? null : $"{cell.Name}: {cell.DisplayText}";
        }

        /// <summary>
        /// The scenario's rights, wrongs and errors, read off the steps that ran — Storyteller's
        /// three counts, and the same vocabulary a Gherkin scenario reports.
        /// </summary>
        /// <remarks>
        /// A step with cells counts its CELLS, not itself: a sentence asserting a sum and a product
        /// made two claims, and reporting it as one right understates what the spec covered exactly
        /// as much as reporting it as one wrong overstates what broke.
        /// </remarks>
        public Counts Counts
        {
            get
            {
                var counts = new Counts();

                foreach (var step in _steps)
                {
                    if (step.Cells.Count > 0)
                    {
                        foreach (var cell in step.Cells) counts.Read(cell.Status);

                        // The failure counts only when no cell already reports one. A projected assertion
                        // produces both — a cell built from the call site and the exception it threw — and
                        // they are one disagreement, not two.
                        if (step.Failure is not null
                            && step.Cells.All(x => x.Status is ResultStatus.success or ResultStatus.ok))
                        {
                            counts.Read(ProjectedFailure.StatusOf(step.Failure));
                        }
                    }
                    else
                    {
                        counts.Read(step.Status);
                    }
                }

                return counts;
            }
        }

        /// <summary>
        /// The failures the steps GATHERED rather than threw — a <see cref="SpecAssert"/> wrong or a
        /// failed cell — described for the runner, or null when there are none.
        /// </summary>
        /// <remarks>
        /// This is the seam that keeps a projected spec honest. Gathering lets the scenario run to
        /// the end and show every wrong; the runner still has to be told, or a red spec would report
        /// as a green test. <see cref="MarkerStepRun.EndScenario"/> turns this into the exception the
        /// adapter throws.
        /// </remarks>
        public string? GatheredFailures()
        {
            // Every step that failed, described or not. A bare fact has no message and is still a
            // failure — filtering on the description would have reported the scenario green.
            var failed = _steps
                .Where(x => x.Status is not ResultStatus.success)
                .Select(x => (Step: x, Description: describe(x)))
                .ToList();

            if (failed.Count == 0) return null;

            var lines = failed.Select(x =>
                x.Description is null ? $"  {x.Step}" : $"  {x.Step} => {x.Description}");
            var heading = failed.Count == 1
                ? "1 specification step failed:"
                : $"{failed.Count} specification steps failed:";

            return heading + Environment.NewLine + string.Join(Environment.NewLine, lines);
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

            // A gathered wrong is a real failure even when nothing threw, so it decides the
            // scenario's outcome exactly as the runner's own verdict does.
            var failure = FailureDescription ?? Failure?.Message ?? GatheredFailures();

            _publisher?.Post(new ScenarioFinished(
                _runId, Uid,
                failure is null ? "CleanPass" : "Failed",
                Attempts: 1,
                DurationMs: _clock.ElapsedMilliseconds,
                ErrorMessage: failure,
                At: DateTimeOffset.UtcNow));

            ScenarioCompleted?.Invoke(this);
        }

        /// <param name="previous">
        /// The step that was open when this one started, restored when it ends. A chain rather than
        /// a stack so a handle disposed out of order cannot leave the wrong step current.
        /// </param>
        private sealed class StepHandle(
            Recording recording, RecordedStep step, Stopwatch clock, RecordedStep? previous) : IStepHandle
        {
            private bool _ended;

            public void Fail(Exception exception) => end(exception);

            public void AddCell(CellResult cell) => step.Cells.Add(cell);

            public void Dispose() => end(null);

            private void end(Exception? failure)
            {
                // Track() fails the step and then its finally disposes it. First call wins, so the
                // failure is not overwritten by the disposal that follows it.
                if (_ended) return;
                _ended = true;

                recording.OpenStep = previous;
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

        /// <summary>
        /// How deeply nested this step is: 0 for a step called straight from the test body, 1 for
        /// one a step helper called in turn.
        /// </summary>
        /// <remarks>
        /// Storyteller's curried and imported grammars are exactly this shape — a short sentence
        /// whose body is another grammar — and so is any helper built out of two others. Flattening
        /// them would render one authored step as two siblings, which reads as the specification
        /// saying the same thing twice.
        /// </remarks>
        public int Depth { get; internal set; }

        /// <summary>The steps this one called, in order.</summary>
        public List<RecordedStep> Children { get; } = new();

        /// <summary>
        /// 1-based position in <see cref="PlannedSteps"/> of the call site this step came from, or
        /// null for a step with no plan behind it.
        /// </summary>
        public int? PlannedStepNumber { get; internal set; }

        /// <summary>
        /// Where this step's input values sit in <see cref="Text"/> — rendered in italics, the way
        /// Storyteller rendered a sentence's input cells.
        /// </summary>
        public IReadOnlyList<StepTextSpan> ValueSpans { get; internal set; } = [];

        /// <summary>
        /// The value comparisons this step reported, through
        /// <see cref="SpecAssert.Check{T}"/> — Storyteller's assertion-sentence cells.
        /// </summary>
        /// <remarks>
        /// A cell is how a projected step says <b>what</b> was wrong rather than only that
        /// something was. Without them the finest verdict a projected step can carry is its
        /// exception, which is one failure per test and no expected/actual pair — a value check
        /// rendered as a bare red line saying nothing a reader could act on.
        /// </remarks>
        public List<CellResult> Cells { get; } = new();

        /// <summary>
        /// The step's verdict, on the same four-way vocabulary the Gherkin lane reports:
        /// a failed cell or an assertion exception is <c>failed</c> (a Storyteller <i>wrong</i>),
        /// any other exception is <c>error</c>, and a step that finished with nothing to say is
        /// <c>success</c>.
        /// </summary>
        /// <remarks>
        /// Derived rather than stored so a cell reported late — a helper that checks several
        /// values before returning — cannot leave a stale verdict behind.
        /// </remarks>
        public ResultStatus Status
        {
            get
            {
                if (Failure is not null) return ProjectedFailure.StatusOf(Failure);
                if (Cells.Any(x => x.Status == ResultStatus.failed)) return ResultStatus.failed;
                if (Cells.Any(x => x.Status is ResultStatus.error or ResultStatus.invalid)) return ResultStatus.error;

                // A step whose inner step failed has failed. The outer sentence is the claim the
                // specification made, so reporting it green over a red child would be the one
                // rendering mistake a reader cannot recover from.
                if (Children.Any(x => x.Status == ResultStatus.error)) return ResultStatus.error;
                if (Children.Any(x => x.Status == ResultStatus.failed)) return ResultStatus.failed;

                return ResultStatus.success;
            }
        }

        /// <summary>Unique within the scenario; the id the wire events key on.</summary>
        public string StepId { get; internal set; } = "";

        /// <summary>
        /// 1-based position in <see cref="Recording.Declared"/> of the marker comment this step
        /// ran inside (issue #304), or null for a step outside every declared region.
        /// </summary>
        public int? DeclaredStepNumber { get; internal set; }

        /// <summary>What the helper threw, when it threw. Null on a step that passed.</summary>
        public Exception? Failure { get; internal set; }

        /// <summary>Null while the step is still running — which is what makes progress visible.</summary>
        public long? DurationMs => EndedAtMs - StartedAtMs;

        public override string ToString()
            => (Keyword.Length > 0 ? Keyword + " " : "") + Text;
    }

    /// <summary>
    /// The handle handed out when no scenario is recording. Every operation is a no-op, so a decorated
    /// helper called outside a specification behaves exactly as it would undecorated.
    /// </summary>
    private sealed class NoStep : IStepHandle
    {
        public static readonly NoStep Instance = new();

        public void Fail(Exception exception) { }

        public void AddCell(CellResult cell) { }

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

    /// <summary>
    /// Attach a comparison to this step, whether or not it is still the ambient open one.
    /// </summary>
    /// <remarks>
    /// On the HANDLE rather than through <c>ScenarioRecorder.CurrentStep</c>, because
    /// <see cref="Fail"/> closes the step and restores whatever was open before it — so a caller that
    /// failed the step first and reached for the ambient one second was adding its cell to the step's
    /// parent, or to nothing at all. Order-independent is the only safe shape here.
    /// </remarks>
    void AddCell(CellResult cell);
}
