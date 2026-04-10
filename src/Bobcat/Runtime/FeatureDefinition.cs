using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// A compiled feature — produced by the source generator, consumed by the runner.
/// </summary>
public class FeatureDefinition
{
    public string Title { get; }
    public Type FixtureType { get; }
    public IReadOnlyList<ScenarioDefinition> Scenarios { get; }

    public FeatureDefinition(string title, Type fixtureType, ScenarioDefinition[] scenarios)
    {
        Title = title;
        FixtureType = fixtureType;
        Scenarios = scenarios;
    }
}

/// <summary>
/// A compiled scenario within a feature.
/// </summary>
public class ScenarioDefinition
{
    public string Title { get; }
    public string[] Tags { get; }
    private readonly Action<Fixture, ExecutionPlan> _buildPlan;

    public ScenarioDefinition(string title, string[] tags, Action<Fixture, ExecutionPlan> buildPlan)
    {
        Title = title;
        Tags = tags;
        _buildPlan = buildPlan;
    }

    public void BuildPlan(Fixture fixture, ExecutionPlan plan)
    {
        _buildPlan(fixture, plan);
    }
}
