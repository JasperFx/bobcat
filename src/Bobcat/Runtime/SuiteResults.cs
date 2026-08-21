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
    private readonly List<NotRunScenario> _notRun = new();

    public IReadOnlyList<FeatureResults> Features => _features;

    /// <summary>
    /// Aggregated on every read rather than at <see cref="Add"/> time, because a feature is
    /// registered here before its scenarios run — so that what it did get through survives a
    /// harness failure part-way through it.
    /// </summary>
    public Counts Counts
    {
        get
        {
            var counts = new Counts();
            foreach (var feature in _features)
            {
                counts.Rights += feature.Counts.Rights;
                counts.Wrongs += feature.Counts.Wrongs;
                counts.Errors += feature.Counts.Errors;
            }

            return counts;
        }
    }

    public void Add(FeatureResults feature) => _features.Add(feature);

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
            if (CatastrophicFailure is not null) return 2;
            if (_features.Any(f => f.WasCatastrophic)) return 2;
            if (_features.Any(f => f.LifecycleFailure is not null)) return 2;
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

    /// <summary>
    /// Set when the harness itself failed: a resource that would not start, a global action's
    /// <c>SetUp</c> that threw, a <c>SpecCatastrophicException</c> from a feature hook, or any
    /// exception that escaped the run's orchestration. Whatever was still to run did not, and
    /// is listed in <see cref="NotRun"/> with this as the reason. Exits 2 — it is reported
    /// through the same path as any other run, never thrown out of <c>RunAll</c>, because a
    /// host that dies with an unhandled exception can only be read as a crash by whatever is
    /// driving it (issue #123).
    /// </summary>
    public string? CatastrophicFailure { get; set; }

    /// <summary>The exception behind <see cref="CatastrophicFailure"/>, when there was one.</summary>
    public Exception? CatastrophicException { get; set; }

    /// <summary>
    /// Scenarios the run planned to execute and did not, each with the reason — suite-level
    /// (nothing ran) and per feature (a <c>BeforeAll</c> that threw). An MTP host reports
    /// every one of these as a test node in error, so a supervisor or <c>dotnet test</c> sees a
    /// verdict with a message rather than silence.
    /// </summary>
    public IReadOnlyList<NotRunScenario> NotRun
        => _notRun.Concat(_features.SelectMany(f => f.NotRun)).ToList();

    public void AddNotRun(NotRunScenario scenario) => _notRun.Add(scenario);

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
    private readonly List<NotRunScenario> _notRun = new();

    public FeatureResults(string title)
    {
        Title = title;
    }

    public IReadOnlyList<ScenarioResult> Scenarios => _scenarios;
    public Counts Counts { get; } = new();

    /// <summary>
    /// Set when the feature's own <c>BeforeAll</c> or <c>AfterAll</c> threw. A <c>BeforeAll</c>
    /// failure means none of the feature's scenarios ran (they are in <see cref="NotRun"/>);
    /// an <c>AfterAll</c> failure leaves the scenario results standing but the feature cannot
    /// be called clean. Either way the run exits 2 — this is a broken harness, not a failing
    /// test — and the run continues with the next feature, the way a critical step failure
    /// aborts its scenario and not the suite. A <c>SpecCatastrophicException</c> from either
    /// hook is still catastrophic for the whole run.
    /// </summary>
    public string? LifecycleFailure { get; set; }

    /// <summary>The exception behind <see cref="LifecycleFailure"/>.</summary>
    public Exception? LifecycleException { get; set; }

    /// <summary>Scenarios of this feature that did not run, and why.</summary>
    public IReadOnlyList<NotRunScenario> NotRun => _notRun;

    public void AddNotRun(NotRunScenario scenario) => _notRun.Add(scenario);

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

/// <summary>
/// A scenario the run meant to execute and did not — because the harness failed before it
/// could, not because anything in the scenario was wrong. Kept distinct from a
/// <see cref="ScenarioResult"/> on purpose: a scenario that never ran has no steps, no
/// counts and no attempts, and synthesizing those would put made-up facts in the report.
/// </summary>
public sealed record NotRunScenario(
    string FeatureTitle,
    string Title,
    string[] Tags,
    string Reason,
    Exception? Cause = null);
