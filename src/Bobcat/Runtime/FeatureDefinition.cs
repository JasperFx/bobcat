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

    /// <summary>
    /// Tags written on the <c>Feature:</c> line itself. Every scenario in the feature inherits
    /// them (its own <see cref="ScenarioDefinition.Tags"/> already include them), so this is for
    /// tooling that wants the feature-level declaration on its own — <c>@slice:</c> / <c>@domain:</c>
    /// via <see cref="SliceTags"/>.
    /// </summary>
    public string[] Tags { get; init; } = [];

    /// <summary>
    /// The free-text description between the <c>Feature:</c> line and the first Background or
    /// Scenario, lines joined with <c>\n</c>, or null. <see cref="SliceTags.TriggeredBy"/> reads
    /// the <c>Triggered by …</c> line out of it.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>The slice this feature declares (<c>@slice:&lt;name&gt;</c>), or null.</summary>
    public string? Slice => SliceTags.Slice(Tags);

    /// <summary>The domain this feature declares (<c>@domain:&lt;name&gt;</c>), or null.</summary>
    public string? Domain => SliceTags.Domain(Tags);

    /// <summary>The trigger named by a <c>Triggered by …</c> description line, or null.</summary>
    public string? TriggeredBy => SliceTags.TriggeredBy(Description);

    /// <summary>
    /// Discovered <c>BeforeEach</c> hooks, emitted by the generator with their parameters
    /// resolved. Runs inside the scenario's DI scope, before the first step.
    /// </summary>
    public Func<Fixture, IStepContext, Task>? BeforeEach { get; init; }

    /// <summary>
    /// Discovered <c>AfterEach</c> hooks. Always runs, even when the scenario failed —
    /// still inside the scenario's DI scope.
    /// </summary>
    public Func<Fixture, IStepContext, Task>? AfterEach { get; init; }

    /// <summary>
    /// Discovered static <c>BeforeAll</c> hooks — once per feature, before any scenario scope
    /// exists. Static because fixture instances are per scenario.
    /// </summary>
    public Func<IStepContext, Task>? BeforeAll { get; init; }

    /// <summary>
    /// Discovered static <c>AfterAll</c> hooks — once per feature, after the last scenario.
    /// </summary>
    public Func<IStepContext, Task>? AfterAll { get; init; }
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
