using Bobcat.Console.Contracts;

namespace Bobcat.Console.Runs;

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

    public int? Passed { get; private set; }
    public int? Failed { get; private set; }
    public int? PassedOnRetry { get; private set; }
    public int? Indeterminate { get; private set; }
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

    public RunProjection(Guid runId)
    {
        RunId = runId;
    }

    public IReadOnlyCollection<ScenarioProjection> Scenarios => _scenarios.Values;

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

    /// <summary>Whether the run has any supervisor topology worth reporting.</summary>
    public bool HasTopology => _lanes.Count > 0 || _recycles.Count > 0 || _workerFaults.Count > 0;

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
                break;

            case RunFinished e:
                Finished = true;
                ExitCode = e.ExitCode;
                FinishedAt = e.FinishedAt;
                Passed = e.Passed;
                Failed = e.Failed;
                PassedOnRetry = e.PassedOnRetry;
                Indeterminate = e.Indeterminate;
                break;

            case ScenarioStarted e:
            {
                var scenario = ensureScenario(e.Uid);
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
                break;
            }

            case ScenarioFinished e:
            {
                var scenario = ensureScenario(e.Uid);
                scenario.Outcome = e.Outcome;
                // Same correction: a worker reporting "1 attempt" is reporting its own count,
                // and a total can never be fewer than the attempts we watched start.
                scenario.Attempts = Math.Max(e.Attempts, scenario.Attempt);
                scenario.DurationMs = e.DurationMs;
                scenario.ErrorMessage = e.ErrorMessage;
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
                ensureScenario(e.Uid).Steps.Add(new StepProjection(e.StepId, e.Kind, e.Text));
                break;

            case StepFinished e:
            {
                var step = ensureScenario(e.Uid).Steps.FirstOrDefault(s => s.StepId == e.StepId);
                if (step != null)
                {
                    step.Status = e.Status;
                    step.DurationMs = e.DurationMs;
                    step.ErrorMessage = e.ErrorMessage;
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

    /// <summary>Steps of the current (or final) attempt.</summary>
    public List<StepProjection> Steps { get; } = new();

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

    /// <summary>Mirrors ResultStatus; "running" until StepFinished arrives.</summary>
    public string Status { get; set; } = "running";

    public long? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }

    public StepProjection(string stepId, string kind, string text)
    {
        StepId = stepId;
        Kind = kind;
        Text = text;
    }
}

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
