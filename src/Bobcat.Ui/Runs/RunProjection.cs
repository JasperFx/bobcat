using Bobcat.Ui.Contracts;

namespace Bobcat.Ui.Runs;

/// <summary>
/// Server-side state of one run, folded from its event stream — the same reduction the
/// frontend's Pinia runs-store performs, kept here so exports (and later, hydration and MCP
/// queries) never depend on a browser having been connected. Deliberately tolerant of
/// out-of-order or missing events: handlers upsert rather than assume prior state.
/// </summary>
public class RunProjection
{
    public Guid RunId { get; }
    public string Suite { get; private set; } = "(unknown suite)";
    public string Repository { get; private set; } = "(unknown)";
    public string? Branch { get; private set; }
    public string Mode { get; private set; } = "unknown";
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public int? TotalScenarios { get; private set; }
    public bool Finished { get; private set; }
    public int? ExitCode { get; private set; }

    /// <summary>The opaque BOBCAT_RUN_TAG correlation tag, if the publisher set one.</summary>
    public string? Tag { get; private set; }

    /// <summary>
    /// The canonical <c>owner/name</c> of <see cref="Repository"/> (stoat#64), resolved by the
    /// registry from the checkout's git remote when the run announces itself. Null for a path that is
    /// not a checkout on this box — a run published from another machine — which is "unattributed",
    /// never a guess.
    /// </summary>
    public string? RepositorySlug { get; internal set; }

    /// <summary>The Claude Code session that started the run (bobcat#389), when one did.</summary>
    public string? Session { get; private set; }

    /// <summary>The monitor command that caused the run (JasperFx/bobcat#392, stoat#77), when one did.</summary>
    public string? Command { get; private set; }

    public int? Passed { get; private set; }
    public int? Failed { get; private set; }
    public int? PassedOnRetry { get; private set; }
    public int? Indeterminate { get; private set; }

    /// <summary>Tests the framework skipped (bobcat#655); null from a publisher that does not say.</summary>
    public int? Skipped { get; private set; }

    public DateTimeOffset LastEventAt { get; private set; }

    /// <summary>
    /// Rehydrated from an archive with no terminal RunFinished — the publisher is gone and
    /// the run will never complete. Cleared by the registry if the run publishes again.
    /// </summary>
    public bool Orphaned { get; internal set; }

    private readonly Dictionary<string, ScenarioProjection> _scenarios = new();
    private readonly List<LaneProjection> _lanes = new();
    private readonly List<RecycleProjection> _recycles = new();
    private readonly List<WorkerFaultProjection> _workerFaults = new();
    private readonly List<StallProjection> _stalls = new();

    public RunProjection(Guid runId)
    {
        RunId = runId;
    }

    /// <summary>
    /// Every scenario a reader should count: the worker's own, plus any forwarded test that is not
    /// a mirror of one (<see cref="ForwardedTestMirror"/>). <see cref="RunningIn"/> joins lane uids
    /// against the full set, since a lane names its tests by the worker's id.
    /// </summary>
    public IReadOnlyCollection<ScenarioProjection> Scenarios => ForwardedTestMirror.Visible(_scenarios.Values);

    // The supervisor's topology (issue #84), folded with exactly the rules the Pinia
    // runs-store applies — SupervisorTopologyProjectionTests ports that store's cases so the two
    // folds cannot drift. An in-process run never receives these events and the three lists
    // stay empty; nothing is inferred for it.

    /// <summary>Supervisor lanes in lane order; empty for an in-process run.</summary>
    public IReadOnlyList<LaneProjection> Lanes => _lanes;

    /// <summary>Resources the supervisor threw away and stood up again, in the order it did so.</summary>
    public IReadOnlyList<RecycleProjection> Recycles => _recycles;

    /// <summary>Worker processes that died, each with the lane, exit code and last standard error.</summary>
    public IReadOnlyList<WorkerFaultProjection> WorkerFaults => _workerFaults;

    /// <summary>
    /// Tests the supervisor reported as stalled (issue #145), in detection order — the name of
    /// the hung test, which is exactly what a capped CI job's log cannot produce.
    /// </summary>
    public IReadOnlyList<StallProjection> Stalls => _stalls;

    /// <summary>
    /// The latest supervisor progress heartbeat (issue #148) — the only live progress a
    /// foreign-framework worker (xUnit, tUnit) gives a run, since it streams no scenario
    /// events. Null until the first run_progress arrives; latest wins.
    /// </summary>
    public RunProgressProjection? Progress { get; private set; }

    /// <summary>Whether the run has any supervisor topology worth reporting.</summary>
    public bool HasTopology =>
        _lanes.Count > 0 || _recycles.Count > 0 || _workerFaults.Count > 0 || _stalls.Count > 0;

    /// <summary>
    /// The scenarios a lane's worker is running right now — the uids of its latest pass joined
    /// to live scenario state. A foreign-framework worker (xUnit, tUnit) streams no scenarios,
    /// so for it this is always empty and the lane itself is the whole signal.
    /// </summary>
    public IReadOnlyList<ScenarioProjection> RunningIn(LaneProjection lane)
        => lane.Uids
            .Select(uid => _scenarios.GetValueOrDefault(uid))
            .Where(s => s is { Outcome: null })
            .Select(s => s!)
            .ToList();

    public void Apply(MonitorEvent @event)
    {
        LastEventAt = DateTimeOffset.UtcNow;

        switch (@event)
        {
            case RunStarted e:
                Suite = e.Suite;
                Repository = e.Repository;
                Branch = e.Branch;
                Mode = e.Mode;
                StartedAt = e.StartedAt;
                TotalScenarios = e.TotalScenarios;
                Tag = e.Tag;
                Session = e.Session;
                Command = e.Command;
                break;

            case RunFinished e:
                Finished = true;
                ExitCode = e.ExitCode;
                FinishedAt = e.FinishedAt;
                Passed = e.Passed;
                Failed = e.Failed;
                PassedOnRetry = e.PassedOnRetry;
                Indeterminate = e.Indeterminate;
                Skipped = e.Skipped;
                break;

            case ScenarioStarted e:
            {
                var scenario = ensureScenario(e.Uid);
                scenario.WorkerPublished = true;
                scenario.Feature = e.Feature;
                scenario.Scenario = e.Scenario;

                // A supervised retry runs in a process that counts its attempts from one — the
                // MTP host builds a fresh runner per run request, so even a same-process retry
                // restarts at one. When a retry was scheduled, that number is the true one: the
                // supervisor is the only thing that knows this start is a second try.
                //
                // Taken as a floor rather than an assignment, so an attempt number never goes
                // backwards. A re-announced start (hydration replaying the archive over live
                // state) must not un-know an attempt we already watched happen.
                var attempt = Math.Max(e.Attempt, Math.Max(scenario.ScheduledAttempt ?? 0, scenario.Attempt));
                scenario.ScheduledAttempt = null;

                if (attempt > scenario.Attempt)
                {
                    // The RetryScheduled that preceded this start usually archived the
                    // attempt already (with the policy's disposition and reason); this is the
                    // fallback for a retry we only learn about from its start event.
                    scenario.ArchivePriorAttempt(scenario.Attempt, disposition: null, reason: null);
                    // A supervised retry's first attempt reported its own terminal outcome
                    // (its worker finished the test — Failed); the scenario is running again
                    // now, and that is what a lane's "running now" and run_status read. Only a
                    // genuinely new attempt clears it, so a replayed start never un-finishes one.
                    scenario.Outcome = null;
                }

                scenario.Attempt = attempt;
                // Every attempt gets a fresh reset/begin/end bracket, so the live step list
                // starts over — earlier attempts survive in PriorAttempts, which is what CTRF's
                // retryAttempts[] is rendered from.
                scenario.Steps.Clear();
                scenario.ErrorMessage = null;
                // Assign, never append, for the same reason TouchedTypes does: Apply runs on live
                // ingest and on archive replay. Null leaves what stands — an older publisher
                // omitting the narrative is not a statement that there isn't one.
                if (e.DeclaredSteps != null) scenario.DeclaredSteps = e.DeclaredSteps;
                // The plan (stoat#59), by the same rule: assign, never clear.
                if (e.PlannedSteps != null) scenario.PlannedSteps = e.PlannedSteps;
                break;
            }

            case ScenarioFinished e:
            {
                var scenario = ensureScenario(e.Uid);
                scenario.WorkerPublished = true;
                scenario.Outcome = e.Outcome;
                // Same correction: a worker reporting "1 attempt" is reporting its own count,
                // and a total can never be fewer than the attempts we watched start.
                scenario.Attempts = Math.Max(e.Attempts, scenario.Attempt);
                scenario.DurationMs = e.DurationMs;
                scenario.ErrorMessage = e.ErrorMessage;
                // Assign, never append: Apply runs on live ingest AND archive replay, and an
                // append would double the evidence on rehydration. Null (an older publisher, or
                // nothing recorded) leaves whatever stands — absence of evidence is not
                // evidence to erase with.
                if (e.TouchedTypes != null) scenario.TouchedTypes = e.TouchedTypes;
                scenario.FinishedAt = e.At;

                // SpecLog lines (bobcat#547) arrive at the close, latched like reports, so they are
                // folded onto the steps they were written in. Assigned whole, so a replay lands the
                // same lines rather than doubled ones.
                foreach (var log in e.Logs ?? [])
                {
                    if (log.StepId is null) continue;

                    var step = scenario.Steps.LastOrDefault(s => s.StepId == log.StepId);
                    if (step is null) continue;

                    step.Logs = log.Suppressed > 0
                        ? [.. log.Lines, log.Suppressed == 1 ? "…and 1 more line" : $"…and {log.Suppressed} more lines"]
                        : log.Lines;
                }

                break;
            }

            case RetryScheduled e:
            {
                var scenario = ensureScenario(e.Uid);
                scenario.RetryReasons.Add(e.Reason);
                scenario.ScheduledAttempt = e.NextAttempt;
                // The attempt that just failed is history the moment a retry is scheduled —
                // snapshot its steps now, while the policy's verdict is in hand.
                scenario.ArchivePriorAttempt(e.NextAttempt - 1, e.Disposition, e.Reason);
                break;
            }

            case StepStarted e:
            {
                var scenario = ensureScenario(e.Uid);
                scenario.WorkerPublished = true;
                scenario.Steps.Add(new StepProjection(e.StepId, e.Kind, e.Text)
                {
                    DeclaredStepNumber = e.DeclaredStepNumber,
                    PlannedStepNumber = e.PlannedStepNumber,
                    Values = e.Values ?? []
                });
                break;
            }

            case StepFinished e:
            {
                // StepId is a step TEMPLATE id — the step method's name — not an identity, and
                // steps repeat within one scenario: `Given {event} occurred` once per arranged
                // event (#259), `And no events for {aggregate} "…"` once per re-pointed stream
                // (#311, #320). Matching on it alone handed every finish to the FIRST occurrence,
                // so the rest stayed "running" forever inside a scenario the runner had already
                // passed — 33 of 182 steps across a 37-scenario CleanPass run (issue #322).
                //
                // The first occurrence still running is the right one: steps are appended in
                // order and finish in order. The fallback keeps the old behaviour for a finish
                // that arrives with nothing left running, rather than dropping it silently.
                var steps = ensureScenario(e.Uid).Steps;
                var step = steps.FirstOrDefault(s => s.StepId == e.StepId && s.Status == StepProjection.Running)
                           ?? steps.LastOrDefault(s => s.StepId == e.StepId);
                if (step != null)
                {
                    step.Status = e.Status;
                    step.DurationMs = e.DurationMs;
                    step.ErrorMessage = e.ErrorMessage;
                    // The step's own report (stoat#58, #60). Assigned whole from the one event that
                    // carries it, so a replayed finish lands the same report rather than a doubled one.
                    step.Cells = e.Cells ?? [];
                    step.Columns = e.Columns ?? [];
                    step.Logs = e.Logs ?? [];
                    step.Diagnostics = e.Diagnostics ?? new Dictionary<string, string>();
                    step.Exception = e.ExceptionType is null && e.StackTrace is null
                        ? null
                        : new StepException(
                            e.ExceptionType, e.ErrorMessage, e.StackFrames ?? [], e.HiddenStackFrames, e.StackTrace);
                }

                break;
            }

            case LaneStarted e:
            {
                var lane = ensureLane(e.Lane, e.At);
                // The supervisor's own clock orders a lane's passes. A start no newer than the
                // one we are already on is a replay — hydration re-announces the archive over
                // live state — and must not count as another pass or reset the lane. Equal
                // means the very same start.
                if (lane.Passes > 0 && e.At <= lane.StartedAt)
                {
                    if (e.At == lane.StartedAt) lane.Uids = e.Uids.ToList();
                    break;
                }

                // A new pass: the first, or a same-process retry handed back to the lane the
                // test ran in.
                lane.Status = LaneProjection.Running;
                lane.Uids = e.Uids.ToList();
                lane.Passes += 1;
                lane.StartedAt = e.At;
                lane.FinishedAt = null;
                lane.Outcomes = null;
                break;
            }

            case LaneFinished e:
            {
                var lane = ensureLane(e.Lane, e.At);
                if (lane.Passes == 0) lane.Passes = 1; // a finish whose start was dropped or never seen
                // A finish older than the pass we are on belongs to an earlier pass — replayed
                // history.
                if (e.At < lane.StartedAt) break;
                lane.Status = e.Crashed ? LaneProjection.Crashed : LaneProjection.Finished;
                lane.FinishedAt = e.At;
                lane.Outcomes = e.Outcomes;
                break;
            }

            case ResourceRecycled e:
                // Replay guard: the same recycle never lands twice.
                if (_recycles.Any(r => r.Resource == e.Resource && r.At == e.At)) break;
                _recycles.Add(new RecycleProjection(e.Resource, e.At));
                break;

            case WorkerFaulted e:
                if (_workerFaults.Any(f => f.At == e.At && f.Lane == e.Lane && f.Fault == e.Fault)) break;
                _workerFaults.Add(new WorkerFaultProjection(e.Lane, e.Fault, e.ExitCode, e.StandardError, e.At));
                break;

            case WorkerStarted e:
            {
                // The pid folds onto the lane it belongs to — lane-to-pid correlation (issue
                // #146). A one-test process has no lane slot here; its pid still travels on
                // any TestStalled/WorkerFaulted it produces.
                if (e.Lane is { } index && e.ProcessId is not null)
                {
                    ensureLane(index, e.At).ProcessId = e.ProcessId;
                }

                break;
            }

            case TestStalled e:
                // Replay guard: hydration re-announces the archive over live state.
                if (_stalls.Any(s => s.Uid == e.Uid && s.At == e.At)) break;
                _stalls.Add(new StallProjection(e.Uid, e.DisplayName, e.InFlightMs, e.Lane, e.At));
                break;

            // The supervisor's forwarding of a worker's live per-test stream (issue #195) —
            // the only per-test progress a run whose workers are not Bobcat runners produces.
            // Both cases stand down for any scenario the worker published itself: that stream
            // is richer (steps, error, evidence, true attempt numbers) and it owns the uid.
            // The guard is a property of the scenario rather than of the run, so it holds in
            // either arrival order — a forwarded verdict that lands first is simply overwritten
            // by the worker's own, and one that lands second is ignored.

            case TestStarted e:
            {
                var scenario = ensureScenario(e.Uid);
                if (scenario.WorkerPublished) break;
                scenario.Forwarded = true;
                scenario.DisplayName = e.DisplayName;
                scenario.Scenario = e.DisplayName;

                // A verdict already stamped later than this start belongs to a run of the test
                // we have already seen finish — replay, or a worker re-announcing a decided
                // node. Only a genuinely newer start reopens it.
                if (scenario.FinishedAt is { } finished && e.At <= finished) break;
                scenario.Outcome = null;
                scenario.FinishedAt = null;
                break;
            }

            case TestFinished e:
            {
                var scenario = ensureScenario(e.Uid);
                if (scenario.WorkerPublished) break;
                scenario.Forwarded = true;
                scenario.DisplayName = e.DisplayName;
                scenario.Scenario = e.DisplayName;
                scenario.Outcome = ForeignTestOutcome.From(e.State);
                scenario.State = e.State;
                scenario.DurationMs = e.DurationMs;
                scenario.FinishedAt = e.At;
                break;
            }

            case RunProgress e:
                // Latest wins, and a replayed older heartbeat never rolls progress backwards —
                // the supervisor's elapsed clock orders them without trusting arrival order.
                if (Progress is { } current && e.ElapsedMs < current.ElapsedMs) break;
                Progress = new RunProgressProjection(
                    e.ElapsedMs, e.Completed, e.Total, e.InFlight,
                    e.LongestRunningUid, e.LongestRunningDisplayName, e.LongestRunningMs,
                    e.PeakWorkerRssBytes, e.At,
                    e.Passed, e.Failed, e.Retried, e.WorkersBusy);
                break;

            // RunHeartbeat only refreshes LastEventAt, already done above.
        }
    }

    private LaneProjection ensureLane(int index, DateTimeOffset at)
    {
        var lane = _lanes.FirstOrDefault(l => l.Lane == index);
        if (lane == null)
        {
            lane = new LaneProjection(index, at);
            _lanes.Add(lane);
            _lanes.Sort((a, b) => a.Lane.CompareTo(b.Lane));
        }

        return lane;
    }

    private ScenarioProjection ensureScenario(string uid)
    {
        if (!_scenarios.TryGetValue(uid, out var scenario))
        {
            var slash = uid.IndexOf('/');
            scenario = new ScenarioProjection(uid)
            {
                Feature = slash > 0 ? uid[..slash] : "",
                Scenario = slash > 0 ? uid[(slash + 1)..] : uid
            };
            _scenarios[uid] = scenario;
        }

        return scenario;
    }
}

public class ScenarioProjection
{
    public string Uid { get; }
    public string Feature { get; set; } = "";
    public string Scenario { get; set; } = "";

    /// <summary>1-based attempt currently (or last) running.</summary>
    public int Attempt { get; set; } = 1;

    /// <summary>
    /// The attempt number a <c>RetryScheduled</c> promised, waiting for its start event. Set by
    /// the retry, consumed by the next <c>ScenarioStarted</c>, and null the rest of the time.
    /// </summary>
    /// <remarks>
    /// Only the supervisor knows a run is a retry. Its worker is a fresh process, or at best a
    /// fresh <c>BobcatRunner</c> in a reused one, and either way starts counting at one — so
    /// without this the dashboard showed the third try as attempt 1.
    /// </remarks>
    public int? ScheduledAttempt { get; set; }

    /// <summary>Total attempts from the terminal ScenarioFinished, when it arrived.</summary>
    public int? Attempts { get; set; }

    /// <summary>Mirrors RunOutcome (CleanPass/PassOnRetry/Failed/Aborted); null while running.</summary>
    public string? Outcome { get; set; }

    public long? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> RetryReasons { get; } = new();

    /// <summary>
    /// Run evidence from the terminal ScenarioFinished (issue #107): the CLR types the scenario
    /// observably touched, in first-touch order. Empty when the publisher recorded nothing —
    /// which is most suites; only evidence-aware fixtures (Bobcat.CritterStack's typed steps)
    /// record anything today.
    /// </summary>
    public IReadOnlyList<TouchedType> TouchedTypes { get; set; } = [];

    /// <summary>When the scenario finished — the stamp a consumer ages the evidence by.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// The worker's own word for the verdict — Passed / Failed / Error / Skipped / Timeout /
    /// Cancelled — when this entry came from the supervisor's forwarded per-test stream
    /// (issue #195). Null for a Bobcat scenario, whose vocabulary is <see cref="Outcome"/>'s.
    /// Kept because "skipped" is a fact the RunOutcome vocabulary has no word for, and folding
    /// it into a pass on the wire would have been the drift this contract avoids.
    /// </summary>
    public string? State { get; set; }

    /// <summary>
    /// True once the running worker published this scenario itself. The supervisor forwards a
    /// lower-fidelity per-test stream for every worker (issue #195), including Bobcat ones that
    /// are already publishing; this is what keeps the two from fighting over one uid.
    /// </summary>
    internal bool WorkerPublished { get; set; }

    /// <summary>
    /// True when this entry came from the supervisor's forwarded per-test stream (issue #195), whose
    /// uid is the worker's test id rather than a spec identity. <see cref="ForwardedTestMirror"/>
    /// reads it, with <see cref="DisplayName"/>, to tell a forwarded copy of a worker's scenario from
    /// a test only the supervisor saw.
    /// </summary>
    internal bool Forwarded { get; set; }

    /// <summary>The worker framework's display name for a forwarded test; null for a Bobcat scenario.</summary>
    internal string? DisplayName { get; set; }

    /// <summary>Steps of the current (or final) attempt.</summary>
    public List<StepProjection> Steps { get; } = new();

    /// <summary>
    /// What the scenario declared it does, in order (issue #304) — the marker-comment narrative,
    /// announced before the test ran a line. Empty for every other authoring style, and for a
    /// publisher that predates the field.
    /// </summary>
    /// <remarks>
    /// These are NOT steps and are deliberately not folded into <see cref="Steps"/>: a declared
    /// step carries no verdict of its own and never gains one. What ran points back at it through
    /// <see cref="StepProjection.DeclaredStepNumber"/>, so a reader sees the sentence the test
    /// wrote with the work that happened underneath it — and a declared step with nothing under it
    /// stays blank, which is the honest rendering of "nothing observed this".
    /// </remarks>
    public IReadOnlyList<DeclaredStepInfo> DeclaredSteps { get; set; } = [];

    /// <summary>
    /// Every step the scenario was GOING to take, in source order (stoat#59) — a projected test's
    /// plan, read off its syntax tree at compile time. A planned step no recorded step points back
    /// at through <see cref="StepProjection.PlannedStepNumber"/> was never reached. Empty for a
    /// scenario with no plan behind it (every <c>.feature</c> scenario today) and for an older publisher.
    /// </summary>
    public IReadOnlyList<PlannedStepInfo> PlannedSteps { get; set; } = [];

    /// <summary>
    /// The planned steps no recorded step of the current attempt reached, as 1-based plan
    /// positions — a set difference, never a guess (stoat#59). Only meaningful once the scenario
    /// has a verdict; while it runs, the rest of the plan is pending rather than skipped.
    /// </summary>
    public IReadOnlyList<int> UnreachedPlannedSteps()
    {
        var reached = Steps
            .Where(s => s.PlannedStepNumber is not null)
            .Select(s => s.PlannedStepNumber!.Value)
            .ToHashSet();

        return Enumerable.Range(1, PlannedSteps.Count).Where(n => !reached.Contains(n)).ToList();
    }

    /// <summary>
    /// Full step history of every attempt that was retried away, in attempt order — the
    /// source for CTRF's retryAttempts[]. The current/final attempt lives in
    /// <see cref="Steps"/>, not here.
    /// </summary>
    public List<AttemptProjection> PriorAttempts { get; } = new();

    /// <summary>
    /// Snapshot the in-flight step list as attempt history. Idempotent per attempt number,
    /// because both RetryScheduled and the next ScenarioStarted call it (whichever the wire
    /// delivers first wins, and the RetryScheduled path carries the policy's verdict).
    /// </summary>
    internal void ArchivePriorAttempt(int attempt, string? disposition, string? reason)
    {
        if (PriorAttempts.Any(a => a.Attempt == attempt)) return;

        PriorAttempts.Add(new AttemptProjection(
            attempt,
            Steps.ToList(),
            Steps.LastOrDefault(s => s.ErrorMessage != null)?.ErrorMessage,
            disposition,
            reason));
    }

    public ScenarioProjection(string uid)
    {
        Uid = uid;
    }
}

/// <summary>One retried-away attempt: its step history plus why the policy retried it.</summary>
public class AttemptProjection
{
    public int Attempt { get; }
    public IReadOnlyList<StepProjection> Steps { get; }

    /// <summary>The last failing step's error — the attempt-level failure message.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Mirrors DispositionKind; null when the archive came from a bare ScenarioStarted.</summary>
    public string? Disposition { get; }

    public string? Reason { get; }

    public AttemptProjection(
        int attempt,
        IReadOnlyList<StepProjection> steps,
        string? errorMessage,
        string? disposition,
        string? reason)
    {
        Attempt = attempt;
        Steps = steps;
        ErrorMessage = errorMessage;
        Disposition = disposition;
        Reason = reason;
    }
}

public class StepProjection
{
    public string StepId { get; }
    public string Kind { get; }
    public string Text { get; }

    /// <summary>The status a step carries until its StepFinished arrives.</summary>
    public const string Running = "running";

    /// <summary>Mirrors ResultStatus; <see cref="Running"/> until StepFinished arrives.</summary>
    public string Status { get; set; } = Running;

    /// <summary>
    /// 1-based index into <see cref="ScenarioProjection.DeclaredSteps"/> of the marker comment
    /// this step ran inside (issue #304), or null for a step that belongs to no declared region.
    /// </summary>
    public int? DeclaredStepNumber { get; set; }

    /// <summary>
    /// 1-based index into <see cref="ScenarioProjection.PlannedSteps"/> of the call site this step
    /// came from (stoat#59), or null for a step with no plan behind it.
    /// </summary>
    public int? PlannedStepNumber { get; set; }

    /// <summary>Where the substituted input values sit in <see cref="Text"/> (stoat#59).</summary>
    public IReadOnlyList<StepValueSpan> Values { get; set; } = [];

    public long? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>The step's named expected/actual comparisons, each judged on its own (stoat#58).</summary>
    public IReadOnlyList<StepCell> Cells { get; set; } = [];

    /// <summary>Column order when the cells form a table; empty for an ordinary sentence.</summary>
    public IReadOnlyList<string> Columns { get; set; } = [];

    /// <summary>What the step logged, in order.</summary>
    public IReadOnlyList<string> Logs { get; set; } = [];

    /// <summary>Named diagnostics the step attached.</summary>
    public IReadOnlyDictionary<string, string> Diagnostics { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// The exception the step ended in (stoat#60), or null — including for an assertion that
    /// merely disagreed, which threw nothing and has no stack.
    /// </summary>
    public StepException? Exception { get; set; }

    public StepProjection(string stepId, string kind, string text)
    {
        StepId = stepId;
        Kind = kind;
        Text = text;
    }
}

/// <summary>
/// An exception a step ended in (stoat#60). <see cref="Frames"/> is Bobcat's filtered stack and
/// <see cref="HiddenFrames"/> how many frames the filter removed — reported, never dropped silently;
/// <see cref="StackTrace"/> is the raw one, kept for "show every frame".
/// </summary>
public record StepException(
    string? Type,
    string? Message,
    IReadOnlyList<string> Frames,
    int HiddenFrames,
    string? StackTrace);

/// <summary>
/// One supervisor lane (issue #84). A lane can be handed work more than once — a same-process
/// retry goes back to the lane the test ran in, carrying only the tests being retried — so
/// <see cref="Uids"/> is "what the lane is working through now", not everything it ever ran,
/// and <see cref="Passes"/> counts how many times it was handed work.
/// </summary>
public class LaneProjection
{
    public const string Running = "running";
    public const string Finished = "finished";
    public const string Crashed = "crashed";

    public int Lane { get; }

    /// <summary>running / finished / crashed — the same three words the dashboard's LaneState uses.</summary>
    public string Status { get; set; } = Running;

    /// <summary>The uids handed to the lane's worker on its latest start.</summary>
    public IReadOnlyList<string> Uids { get; set; } = [];

    /// <summary>How many times the lane has been handed work: 1 is the first pass, more are retry passes.</summary>
    public int Passes { get; set; }

    /// <summary>The supervisor's clock at the latest start — what orders the passes on replay.</summary>
    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Outcomes the worker reported on its latest finish; null while it is still running.</summary>
    public int? Outcomes { get; set; }

    /// <summary>
    /// The OS pid of the lane's worker process (issue #146), from worker_started — the handle
    /// an external diagnostic must be pointed at. A lane whose worker is replaced (crash, then
    /// relaunch) is updated by the replacement's own worker_started.
    /// </summary>
    public int? ProcessId { get; set; }

    public LaneProjection(int lane, DateTimeOffset startedAt)
    {
        Lane = lane;
        StartedAt = startedAt;
    }
}

/// <summary>A supervisor-owned resource thrown away and stood up again before a retry.</summary>
public record RecycleProjection(string Resource, DateTimeOffset At);

/// <summary>
/// A worker process that died: the lane it was running (null for a one-test isolated or recycled
/// process), the report's sentence, and the exit code and last standard error as the separate
/// facts a person wants at 2am.
/// </summary>
public record WorkerFaultProjection(
    int? Lane,
    string Fault,
    int? ExitCode,
    string? StandardError,
    DateTimeOffset At);

/// <summary>
/// A test the supervisor reported stalled (issue #145): the name a hung run's log otherwise
/// cannot produce, how long it had been in flight at detection, and where it was running.
/// </summary>
public record StallProjection(
    string Uid,
    string DisplayName,
    long InFlightMs,
    int? Lane,
    DateTimeOffset At);

/// <summary>
/// The supervisor's latest progress heartbeat (issue #148). The longest-running trio is null
/// when nothing was in flight; PeakWorkerRssBytes is null unless memory sampling (issue #149)
/// was on — unmeasured is never zero.
/// </summary>
public record RunProgressProjection(
    long ElapsedMs,
    int Completed,
    int Total,
    int InFlight,
    string? LongestRunningUid,
    string? LongestRunningDisplayName,
    long? LongestRunningMs,
    long? PeakWorkerRssBytes,
    DateTimeOffset At,
    // The verdict split (bobcat#767); null from a publisher that predates it.
    int? Passed = null,
    int? Failed = null,
    int? Retried = null,
    int? WorkersBusy = null);
