using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Test harness that executes a generated <see cref="FeatureDefinition"/> end-to-end
/// (the generator compiled the .feature file into it) without console rendering.
/// </summary>
public static class Specs
{
    /// <param name="instance">
    /// The fixture to run against, when the test wants to read its state afterwards. Built like the
    /// runner would when omitted.
    /// </param>
    public static async Task<ExecutionResults> Run(FeatureDefinition feature, string scenarioTitle,
        Fixture? instance = null)
    {
        var scenario = feature.Scenarios.FirstOrDefault(s => s.Title == scenarioTitle)
                       ?? throw new ArgumentException(
                           $"No scenario '{scenarioTitle}' in feature '{feature.Title}'. " +
                           $"Available: {string.Join(", ", feature.Scenarios.Select(s => s.Title))}");

        var fixture = instance ?? (Fixture)Activator.CreateInstance(feature.FixtureType)!;

        var plan = new ExecutionPlan(scenario.Title, TimeSpan.FromSeconds(30));
        scenario.BuildPlan(fixture, plan);

        var context = new SpecExecutionContext(scenario.Title, resources: new TestResources());
        fixture.Context = context;

        // Mirror the runner: fresh controllable clock per scenario.
        BobcatClock.ResetToControllable();

        if (feature.BeforeEach != null) await feature.BeforeEach(fixture, context);
        try
        {
            var executor = new Executor([new FailureLevelContinuationRule()]);
            await executor.Execute(plan, context);
        }
        finally
        {
            if (feature.AfterEach != null) await feature.AfterEach(fixture, context);
        }

        return context.Results;
    }

    /// <summary>Find a step result by its Gherkin text (or a substring of it).</summary>
    public static StepResult Step(this ExecutionResults results, string textContains)
        => results.Steps.FirstOrDefault(s => s.StepText != null && s.StepText.Contains(textContains))
           ?? throw new ArgumentException(
               $"No step matching '{textContains}'. Steps: {string.Join(" | ", results.Steps.Select(s => s.StepText))}");
}
