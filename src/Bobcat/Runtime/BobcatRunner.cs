using System.Reflection;
using Bobcat.Engine;
using Bobcat.Rendering;

namespace Bobcat.Runtime;

/// <summary>
/// Entry point for running Bobcat specs from a Program.cs.
/// Discovers generated FeatureDefinitions, manages resources, executes scenarios.
/// </summary>
public class BobcatRunner
{
    private readonly TestSuite _suite = new();
    private readonly List<FeatureDefinition> _features = new();
    private readonly CommandLineRenderer _renderer = new();
    private IExecutionObserver _observer = NullObserver.Instance;

    public TestSuite Suite => _suite;
    internal bool SuppressConsoleOutput { get; set; }

    public BobcatRunner WithObserver(IExecutionObserver observer)
    {
        _observer = observer;
        return this;
    }

    /// <summary>
    /// Register a feature definition (typically from generated code).
    /// </summary>
    public BobcatRunner AddFeature(FeatureDefinition feature)
    {
        _features.Add(feature);
        return this;
    }

    /// <summary>
    /// Scan an assembly for all generated FeatureDefinition factories.
    /// Looks for static classes with a public static Define() method returning FeatureDefinition.
    /// </summary>
    public BobcatRunner ScanForFeatures(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || !type.IsAbstract || !type.IsSealed) continue; // static classes

            var defineMethod = type.GetMethod("Define", BindingFlags.Public | BindingFlags.Static);
            if (defineMethod == null || defineMethod.ReturnType != typeof(FeatureDefinition)) continue;

            var feature = (FeatureDefinition?)defineMethod.Invoke(null, null);
            if (feature != null)
            {
                _features.Add(feature);
            }
        }

        return this;
    }

    /// <summary>
    /// Run all features matching the optional filters.
    /// </summary>
    public async Task<SuiteResults> RunAll(
        string? featureFilter = null,
        string? tagFilter = null)
    {
        var suiteResults = new SuiteResults();

        await _suite.StartAll();

        try
        {
            var features = _features.AsEnumerable();

            if (featureFilter != null)
            {
                features = features.Where(f =>
                    f.Title.Contains(featureFilter, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var feature in features)
            {
                var featureResults = await RunFeature(feature, tagFilter);
                suiteResults.Add(featureResults);

                // Stop on catastrophic
                if (featureResults.WasCatastrophic) break;
            }
        }
        finally
        {
            await _suite.DisposeAsync();
        }

        return suiteResults;
    }

    private async Task<FeatureResults> RunFeature(FeatureDefinition feature, string? tagFilter)
    {
        var featureResults = new FeatureResults(feature.Title);
        if (!SuppressConsoleOutput) _renderer.RenderFeatureHeader(feature.Title);
        _observer.FeatureStarted(feature.Title);

        var scenarios = feature.Scenarios.AsEnumerable();

        if (tagFilter != null)
        {
            scenarios = scenarios.Where(s =>
                s.Tags.Any(t => t.Equals(tagFilter, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var scenario in scenarios)
        {
            await _suite.ResetAll();

            var result = await RunScenario(feature, scenario);
            featureResults.Add(result);

            // Render immediately (unless suppressed for JSON mode)
            if (!SuppressConsoleOutput)
            {
                var specRender = SpecRender.FromResults(scenario.Title, result.Results, feature.Title);
                _renderer.Render(specRender);
            }

            // Stop feature on catastrophic
            if (result.Results.Steps.Any(s => s.FailureLevel == FailureLevel.Catastrophic))
                break;
        }

        _observer.FeatureFinished(feature.Title);
        return featureResults;
    }

    private async Task<ScenarioResult> RunScenario(FeatureDefinition feature, ScenarioDefinition scenario)
    {
        var fixture = (Fixture)Activator.CreateInstance(feature.FixtureType)!;

        var timeout = SpecTags.GetTimeout(scenario.Tags) ?? TimeSpan.FromSeconds(30);
        var plan = new ExecutionPlan(scenario.Title, timeout);
        scenario.BuildPlan(fixture, plan);

        var context = new SpecExecutionContext(scenario.Title, suite: _suite);
        fixture.Context = context;

        _observer.ScenarioStarted(feature.Title, scenario.Title);

        await fixture.SetUp();

        try
        {
            var executor = new Executor([new FailureLevelContinuationRule()], _observer);
            await executor.Execute(plan, context);
        }
        finally
        {
            await fixture.TearDown();
        }

        _observer.ScenarioFinished(context.Results);

        return new ScenarioResult(scenario.Title, scenario.Tags, context.Results);
    }

    /// <summary>
    /// List all discovered features and scenarios.
    /// </summary>
    public void ListFeatures()
    {
        foreach (var feature in _features)
        {
            Console.WriteLine($"Feature: {feature.Title}");
            Console.WriteLine($"  Fixture: {feature.FixtureType.Name}");
            foreach (var scenario in feature.Scenarios)
            {
                var tags = scenario.Tags.Length > 0
                    ? " " + string.Join(" ", scenario.Tags.Select(t => $"@{t}"))
                    : "";
                Console.WriteLine($"  - {scenario.Title}{tags}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Print summary counts for the suite run.
    /// </summary>
    public void RenderSummary(SuiteResults results)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════");
        _renderer.RenderCounts(results.Counts);

        var total = results.Features.SelectMany(f => f.Scenarios).Count();
        var passed = results.Features.SelectMany(f => f.Scenarios).Count(s => s.Results.Counts.Succeeded);
        Console.WriteLine($"  {passed}/{total} scenarios passed");
    }

    // --- Convenience static entry point ---

    /// <summary>
    /// Quick entry point for Program.cs:
    /// await BobcatRunner.Run(args, runner => { ... });
    /// </summary>
    public static async Task<int> Run(string[] args, Action<BobcatRunner> configure)
    {
        var runner = new BobcatRunner();
        configure(runner);

        // Simple arg parsing (will move to JasperFx commands later)
        var command = args.Length > 0 ? args[0] : "run";

        if (command == "list")
        {
            runner.ListFeatures();
            return 0;
        }

        string? featureFilter = null;
        string? tagFilter = null;
        bool jsonOutput = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--feature" && i + 1 < args.Length)
                featureFilter = args[++i];
            if (args[i] == "--tag" && i + 1 < args.Length)
                tagFilter = args[++i];
            if (args[i] == "--json")
                jsonOutput = true;
        }

        runner.SuppressConsoleOutput = jsonOutput;
        var results = await runner.RunAll(featureFilter, tagFilter);

        if (jsonOutput)
        {
            Console.WriteLine(Rendering.JsonRenderer.RenderSuite(results));
        }
        else
        {
            runner.RenderSummary(results);
        }

        return results.ExitCode;
    }
}
