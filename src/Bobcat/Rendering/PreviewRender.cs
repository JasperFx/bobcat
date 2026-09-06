using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat.Rendering;

/// <summary>
/// A scenario's execution plan rendered without executing it (issue #208): the steps as the
/// engine would run them, each with the compile-time binding the generator recorded. Built
/// purely from plan composition — no resource is started, the same rule as MTP discovery.
/// </summary>
public class PreviewRender
{
    public string Title { get; init; } = "";
    public string? FeatureTitle { get; init; }
    public string[] Tags { get; init; } = [];
    public List<PreviewStepRender> Steps { get; init; } = new();

    /// <summary>
    /// Set when the plan could not even be composed — a fixture constructor or a code-first
    /// scenario method that threw. The preview reports it instead of crashing the command.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Builds the plan exactly the way a run's attempt would — fresh fixture, pure in-memory
    /// composition — and reads it instead of executing it.
    /// </summary>
    public static PreviewRender FromScenario(FeatureDefinition feature, ScenarioDefinition scenario)
    {
        try
        {
            var fixture = (Fixture)Activator.CreateInstance(feature.FixtureType)!;
            var plan = new ExecutionPlan(scenario.Title, TimeSpan.FromSeconds(30));
            scenario.BuildPlan(fixture, plan);

            return new PreviewRender
            {
                Title = scenario.Title,
                FeatureTitle = feature.Title,
                Tags = scenario.Tags,
                Steps = plan.Steps.Select(PreviewStepRender.FromStep).ToList()
            };
        }
        catch (Exception e)
        {
            // Activator wraps a throwing fixture constructor; the inner exception is the story.
            var cause = (e as System.Reflection.TargetInvocationException)?.InnerException ?? e;
            return new PreviewRender
            {
                Title = scenario.Title,
                FeatureTitle = feature.Title,
                Tags = scenario.Tags,
                Error = $"Could not build the plan: {cause.GetType().Name}: {cause.Message}"
            };
        }
    }
}

/// <summary>One planned step with its compile-time binding, when the step carries one.</summary>
public class PreviewStepRender
{
    public string StepId { get; init; } = "";
    public StepKind Kind { get; init; }
    public string StepText { get; init; } = "";
    public StepBinding? Binding { get; init; }

    public static PreviewStepRender FromStep(IExecutionStep step)
        => step is DelegateExecutionStep d
            ? new PreviewStepRender { StepId = d.StepId, Kind = d.StepKind, StepText = d.StepText, Binding = d.Binding }
            : new PreviewStepRender { StepId = step.StepId, Kind = step.StepKind, StepText = step.StepId };
}
