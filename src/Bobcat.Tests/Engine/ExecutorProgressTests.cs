using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Engine;

public class ExecutorProgressTests
{
    private static ExecutionPlan PlanWith(IExecutionStep step)
    {
        var plan = new ExecutionPlan("spec-1", TimeSpan.FromSeconds(30));
        plan.Add(step);
        return plan;
    }

    [Fact]
    public async Task interim_progress_reported_by_a_step_reaches_the_observer_tagged_with_the_step_id()
    {
        var step = new DelegateExecutionStep("step-1", StepKind.When, "polling for a value",
            async (ctx, _, _) =>
            {
                ctx.ReportProgress(new StepUpdate("waiting… last value 1"));
                ctx.ReportProgress(new StepUpdate("waiting… last value 3"));
                await Task.CompletedTask;
            });

        var observer = new RecordingObserver();
        var executor = new Executor(Array.Empty<IContinuationRule>(), observer);

        await executor.Execute(PlanWith(step), new SpecExecutionContext("spec-1"));

        observer.Progress.Select(x => x.stepId).ShouldAllBe(id => id == "step-1");
        observer.Progress.Select(x => x.update.Message)
            .ShouldBe(new[] { "waiting… last value 1", "waiting… last value 3" });
    }

    [Fact]
    public async Task progress_sink_is_cleared_after_a_step_finishes()
    {
        IStepContext? captured = null;
        var step = new DelegateExecutionStep("step-1", StepKind.When, "capture context",
            (ctx, _, _) =>
            {
                captured = ctx;
                return Task.CompletedTask;
            });

        var observer = new RecordingObserver();
        await new Executor(Array.Empty<IContinuationRule>(), observer)
            .Execute(PlanWith(step), new SpecExecutionContext("spec-1"));

        // Reporting after the step has ended must not leak onto the observer.
        captured.ShouldNotBeNull();
        captured!.ReportProgress(new StepUpdate("too late"));

        observer.Progress.ShouldBeEmpty();
    }

    [Fact]
    public async Task reporting_progress_with_no_running_step_is_a_safe_no_op()
    {
        var context = new SpecExecutionContext("spec-1");

        Should.NotThrow(() => context.ReportProgress(new StepUpdate("nobody listening")));
        await Task.CompletedTask;
    }

    private class RecordingObserver : IExecutionObserver
    {
        public List<(string stepId, StepUpdate update)> Progress { get; } = new();

        public void FeatureStarted(string featureTitle) { }
        public void FeatureFinished(string featureTitle) { }
        public void ScenarioStarted(string featureTitle, string scenarioTitle) { }
        public void StepStarted(string stepId, StepKind kind, string stepText) { }
        public void StepProgress(string stepId, StepUpdate update) => Progress.Add((stepId, update));
        public void StepFinished(StepResult result) { }
        public void ScenarioFinished(ExecutionResults results) { }
    }
}
