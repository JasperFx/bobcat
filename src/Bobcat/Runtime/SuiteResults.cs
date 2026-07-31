using JasperFx.Testing;
using Bobcat.Engine;
using Bobcat.Resilience;

namespace Bobcat.Runtime;

/// <summary>
/// Aggregates results across all features and scenarios in a suite run.
/// </summary>
public class SuiteResults
{
    private readonly List<FeatureResults> _features = new();

    public IReadOnlyList<FeatureResults> Features => _features;
    public Counts Counts { get; } = new();

    public void Add(FeatureResults feature)
    {
        _features.Add(feature);
        Counts.Rights += feature.Counts.Rights;
        Counts.Wrongs += feature.Counts.Wrongs;
        Counts.Errors += feature.Counts.Errors;
    }

    /// <summary>
    /// Exit code per the design doc: 0 = regression pass, 1 = regression fail, 2 = catastrophic.
    /// </summary>
    /// <remarks>
    /// A scenario that passed on retry still exits 0 — it did pass. It is reported separately
    /// via <see cref="PassedOnRetry"/> rather than being folded into the clean passes, so green
    /// CI never hides the fact that something needed three goes.
    /// </remarks>
    public int ExitCode
    {
        get
        {
            if (PreflightFailure is not null) return 2;
            if (_features.Any(f => f.WasCatastrophic)) return 2;
            if (_features.Any(f => f.WasAborted)) return 2;
            // Only regression failures break the build
            if (_features.Any(f => f.HasRegressionFailure)) return 1;
            return 0;
        }
    }

    /// <summary>
    /// Set when the environment preflight failed, in which case no feature ran at all. Exits 2:
    /// a broken harness is not the same fact as failing tests.
    /// </summary>
    public string? PreflightFailure { get; set; }

    public IEnumerable<ScenarioResult> AllScenarios => _features.SelectMany(f => f.Scenarios);

    /// <summary>Scenarios that passed only after retrying — the flakiness ledger for this run.</summary>
    public IReadOnlyList<ScenarioResult> PassedOnRetry
        => AllScenarios.Where(s => s.Outcome == RunOutcome.PassOnRetry).ToList();

    /// <summary>Retries actually performed across the run.</summary>
    public int RetriesPerformed => AllScenarios.Sum(s => s.AttemptCount - 1);

    /// <summary>
    /// Retry dispositions a policy asked for that this runner could not act on — a fresh
    /// process or a resource recycle. Surfaced so the report never implies work that never
    /// happened.
    /// </summary>
    public IReadOnlyList<string> UnsupportedDispositions
        => AllScenarios.SelectMany(s => s.UnsupportedDispositions).Distinct().ToList();
}

public class FeatureResults
{
    public string Title { get; }
    private readonly List<ScenarioResult> _scenarios = new();

    public FeatureResults(string title)
    {
        Title = title;
    }

    public IReadOnlyList<ScenarioResult> Scenarios => _scenarios;
    public Counts Counts { get; } = new();

    public void Add(ScenarioResult scenario)
    {
        _scenarios.Add(scenario);
        Counts.Rights += scenario.Results.Counts.Rights;
        Counts.Wrongs += scenario.Results.Counts.Wrongs;
        Counts.Errors += scenario.Results.Counts.Errors;
    }

    public bool WasCatastrophic =>
        _scenarios.Any(s => s.Results.Steps.Any(r => r.FailureLevel == FailureLevel.Catastrophic));

    /// <summary>A policy returned <see cref="DispositionKind.AbortRun"/> for one of these scenarios.</summary>
    public bool WasAborted => _scenarios.Any(s => s.Outcome == RunOutcome.Aborted);

    public bool HasRegressionFailure =>
        _scenarios.Where(s => SpecTags.IsRegression(s.Tags))
            .Any(s => !s.Results.Counts.Succeeded);
}

public class ScenarioResult
{
    public string Title { get; }
    public string[] Tags { get; }

    /// <summary>Results of the FINAL attempt — what the scenario ultimately did.</summary>
    public ExecutionResults Results { get; }

    /// <summary>
    /// Every attempt in order, with the disposition decided after each. Always at least one
    /// entry; more than one means the scenario was retried.
    /// </summary>
    public IReadOnlyList<AttemptRecord> Attempts { get; init; } = [];

    public ScenarioResult(string title, string[] tags, ExecutionResults results)
    {
        Title = title;
        Tags = tags;
        Results = results;
    }

    public int AttemptCount => Math.Max(1, Attempts.Count);

    public bool WasRetried => AttemptCount > 1;

    /// <summary>
    /// The honest three-way status. A scenario that needed retries reports
    /// <see cref="RunOutcome.PassOnRetry"/>, never <see cref="RunOutcome.CleanPass"/>.
    /// </summary>
    public RunOutcome Outcome
    {
        get
        {
            if (Attempts.Any(a => a.Disposition.Kind == DispositionKind.AbortRun))
                return RunOutcome.Aborted;

            if (!Results.Counts.Succeeded) return RunOutcome.Failed;

            return WasRetried ? RunOutcome.PassOnRetry : RunOutcome.CleanPass;
        }
    }

    /// <summary>Dispositions a policy asked for that the runner could not honour.</summary>
    public IEnumerable<string> UnsupportedDispositions
        => Attempts.Where(a => a.Unsupported is not null).Select(a => a.Unsupported!);
}
