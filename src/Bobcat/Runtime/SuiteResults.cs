using Bobcat.Engine;

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
    public int ExitCode
    {
        get
        {
            if (_features.Any(f => f.WasCatastrophic)) return 2;
            // Only regression failures break the build
            if (_features.Any(f => f.HasRegressionFailure)) return 1;
            return 0;
        }
    }
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

    public bool HasRegressionFailure =>
        _scenarios.Where(s => SpecTags.IsRegression(s.Tags))
            .Any(s => !s.Results.Counts.Succeeded);
}

public class ScenarioResult
{
    public string Title { get; }
    public string[] Tags { get; }
    public ExecutionResults Results { get; }

    public ScenarioResult(string title, string[] tags, ExecutionResults results)
    {
        Title = title;
        Tags = tags;
        Results = results;
    }
}
