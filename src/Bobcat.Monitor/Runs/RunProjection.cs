using Bobcat.Monitor.Contracts;

namespace Bobcat.Monitor.Runs;

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

    /// <summary>"{plan}/{node}" — the coordination test-run-gate this run reports into.</summary>
    public string? PlanNode { get; private set; }

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

    public RunProjection(Guid runId)
    {
        RunId = runId;
    }

    public IReadOnlyCollection<ScenarioProjection> Scenarios => _scenarios.Values;

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
                PlanNode = e.PlanNode;
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
                if (e.Attempt > scenario.Attempt)
                {
                    // The RetryScheduled that preceded this start usually archived the
                    // attempt already (with the policy's disposition and reason); this is the
                    // fallback for a retry we only learn about from its start event.
                    scenario.ArchivePriorAttempt(scenario.Attempt, disposition: null, reason: null);
                }

                scenario.Attempt = e.Attempt;
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
                scenario.Attempts = e.Attempts;
                scenario.DurationMs = e.DurationMs;
                scenario.ErrorMessage = e.ErrorMessage;
                break;
            }

            case RetryScheduled e:
            {
                var scenario = ensureScenario(e.Uid);
                scenario.RetryReasons.Add(e.Reason);
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

            // RunHeartbeat only refreshes LastEventAt, already done above.
        }
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
