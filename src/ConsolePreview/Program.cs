using Bobcat;
using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using ConsolePreview;

var renderer = new CommandLineRenderer();

// Discover generated feature definitions
// The source generator creates Calculator_Feature.Define() from Calculator.feature + CalculatorFixture
var feature = Calculator_Feature.Define();

Console.WriteLine($"Feature: {feature.Title}");
Console.WriteLine($"Fixture: {feature.FixtureType.Name}");
Console.WriteLine($"Scenarios: {feature.Scenarios.Count}");
Console.WriteLine();

foreach (var scenario in feature.Scenarios)
{
    // Create a fresh fixture instance per scenario
    var fixture = (Fixture)Activator.CreateInstance(feature.FixtureType)!;

    // Build the execution plan
    var plan = new ExecutionPlan(scenario.Title, TimeSpan.FromSeconds(30));
    scenario.BuildPlan(fixture, plan);

    // Execute
    var context = new SpecExecutionContext(scenario.Title);
    fixture.Context = context;
    var executor = new Executor([new FailureLevelContinuationRule()]);
    await executor.Execute(plan, context);

    // Render via intermediate model
    var specRender = SpecRender.FromResults(scenario.Title, context.Results, feature.Title);
    renderer.Render(specRender);
}
