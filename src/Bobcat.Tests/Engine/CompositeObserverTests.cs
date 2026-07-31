using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Engine;

public class CompositeObserverTests
{
    /// <summary>Records which callbacks fired, in order, so forwarding can be asserted.</summary>
    private sealed class RecordingObserver : IExecutionObserver
    {
        public List<string> Calls { get; } = new();

        public void RunStarted(int totalScenarios) => Calls.Add($"RunStarted:{totalScenarios}");
        public void RunFinished(SuiteResults results) => Calls.Add("RunFinished");
        public void FeatureStarted(string featureTitle) => Calls.Add($"FeatureStarted:{featureTitle}");
        public void FeatureFinished(string featureTitle) => Calls.Add($"FeatureFinished:{featureTitle}");
        public void ScenarioStarted(string featureTitle, string scenarioTitle) => Calls.Add($"ScenarioStarted:{scenarioTitle}");
        public void StepStarted(string stepId, StepKind kind, string stepText) => Calls.Add($"StepStarted:{stepId}");
        public void StepProgress(string stepId, StepUpdate update) => Calls.Add($"StepProgress:{stepId}");
        public void StepFinished(StepResult result) => Calls.Add($"StepFinished:{result.StepId}");
        public void ScenarioFinished(ExecutionResults results) => Calls.Add("ScenarioFinished");
        public void ScenarioRetrying(string scenarioTitle, int nextAttempt, string reason) => Calls.Add($"ScenarioRetrying:{nextAttempt}");
        public void ScenarioCompleted(string featureTitle, ScenarioResult result) => Calls.Add($"ScenarioCompleted:{result.Title}");
    }

    private sealed class ThrowingObserver : IExecutionObserver
    {
        public void FeatureStarted(string featureTitle) => throw new InvalidOperationException("boom");
        public void FeatureFinished(string featureTitle) => throw new InvalidOperationException("boom");
        public void ScenarioStarted(string featureTitle, string scenarioTitle) => throw new InvalidOperationException("boom");
        public void StepStarted(string stepId, StepKind kind, string stepText) => throw new InvalidOperationException("boom");
        public void StepProgress(string stepId, StepUpdate update) => throw new InvalidOperationException("boom");
        public void StepFinished(StepResult result) => throw new InvalidOperationException("boom");
        public void ScenarioFinished(ExecutionResults results) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void forwards_every_callback_to_every_observer_in_order()
    {
        var first = new RecordingObserver();
        var second = new RecordingObserver();
        var composite = new CompositeObserver(first, second);

        composite.RunStarted(7);
        composite.FeatureStarted("F");
        composite.ScenarioStarted("F", "S");
        composite.StepStarted("s1", StepKind.Given, "a thing");
        composite.StepProgress("s1", new StepUpdate("waiting"));
        composite.ScenarioRetrying("S", 2, "flaky");
        composite.FeatureFinished("F");
        composite.RunFinished(new SuiteResults());

        var expected = new[]
        {
            "RunStarted:7", "FeatureStarted:F", "ScenarioStarted:S", "StepStarted:s1",
            "StepProgress:s1", "ScenarioRetrying:2", "FeatureFinished:F", "RunFinished"
        };
        first.Calls.ShouldBe(expected);
        second.Calls.ShouldBe(expected);
    }

    [Fact]
    public void an_observer_that_throws_does_not_starve_the_observers_after_it()
    {
        var survivor = new RecordingObserver();
        var composite = new CompositeObserver(new ThrowingObserver(), survivor);

        Should.NotThrow(() =>
        {
            composite.FeatureStarted("F");
            composite.ScenarioStarted("F", "S");
            composite.FeatureFinished("F");
        });

        survivor.Calls.ShouldBe(["FeatureStarted:F", "ScenarioStarted:S", "FeatureFinished:F"]);
    }

    [Fact]
    public async Task add_observer_lets_two_observers_watch_the_same_run()
    {
        var first = new RecordingObserver();
        var second = new RecordingObserver();

        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(passingFeature());
        runner.AddObserver(first);
        runner.AddObserver(second);

        await runner.RunAll();

        foreach (var observer in new[] { first, second })
        {
            observer.Calls.ShouldContain("RunStarted:1");
            observer.Calls.ShouldContain("ScenarioStarted:passes");
            observer.Calls.ShouldContain("RunFinished");
        }
    }

    [Fact]
    public async Task add_observer_composes_with_an_observer_registered_via_with_observer()
    {
        var replaced = new RecordingObserver();
        var added = new RecordingObserver();

        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(passingFeature());
        runner.WithObserver(replaced);
        runner.AddObserver(added);

        await runner.RunAll();

        replaced.Calls.ShouldContain("ScenarioStarted:passes");
        added.Calls.ShouldContain("ScenarioStarted:passes");
    }

    public class ObserverFixture : Fixture;

    private static FeatureDefinition passingFeature()
    {
        var scenario = new ScenarioDefinition("passes", [], (_, plan) =>
        {
            plan.Add(new DelegateExecutionStep("step-1", StepKind.Then, "it passes",
                (_, _, _) => Task.CompletedTask));
        });

        return new FeatureDefinition("Observed Feature", typeof(ObserverFixture), [scenario]);
    }
}
