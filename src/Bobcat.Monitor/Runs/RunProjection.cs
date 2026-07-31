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
                break;

            case RunFinished e:
                Finished = true;
                ExitCode = e.ExitCode;
                FinishedAt = e.FinishedAt;
                break;

            case ScenarioStarted e:
            {
                var scenario = ensureScenario(e.Uid);
                scenario.Feature = e.Feature;
                scenario.Scenario = e.Scenario;
                scenario.Attempt = e.Attempt;
                // Every attempt gets a fresh reset/begin/end bracket, so the live step list
                // starts over; earlier attempts stay summarized by attempt count + retry
                // reasons rather than as full step history.
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

    /// <summary>Steps of the current (or final) attempt only.</summary>
    public List<StepProjection> Steps { get; } = new();

    public ScenarioProjection(string uid)
    {
        Uid = uid;
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
