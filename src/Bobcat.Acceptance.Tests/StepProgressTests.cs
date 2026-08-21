using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #99 — the generated code reports progress from inside one long step: a
/// <c>[TableGrammar]</c> ticks through its rows, a <c>[WaitFor]</c> narrates its poll loop.
/// Both ride <see cref="IExecutionObserver.StepProgress"/>, which is what the monitor publisher
/// puts on the wire.
/// </summary>
public class StepProgressTests
{
    private sealed class RecordingObserver : IExecutionObserver
    {
        public List<(string StepId, StepUpdate Update)> Progress { get; } = new();

        public void FeatureStarted(string featureTitle) { }
        public void FeatureFinished(string featureTitle) { }
        public void ScenarioStarted(string featureTitle, string scenarioTitle) { }
        public void StepStarted(string stepId, StepKind kind, string stepText) { }
        public void StepProgress(string stepId, StepUpdate update) => Progress.Add((stepId, update));
        public void StepFinished(StepResult result) { }
        public void ScenarioFinished(ExecutionResults results) { }
    }

    private static async Task<RecordingObserver> run(FeatureDefinition feature, string scenarioTitle)
    {
        var scenario = feature.Scenarios.Single(s => s.Title == scenarioTitle);
        var fixture = (Fixture)Activator.CreateInstance(feature.FixtureType)!;

        var plan = new ExecutionPlan(scenario.Title, TimeSpan.FromSeconds(30));
        scenario.BuildPlan(fixture, plan);

        var context = new SpecExecutionContext(scenario.Title, suite: new TestSuite());
        fixture.Context = context;
        BobcatClock.ResetToControllable();

        var observer = new RecordingObserver();
        await new Executor([new FailureLevelContinuationRule()], observer).Execute(plan, context);
        return observer;
    }

    [Fact]
    public async Task a_table_grammar_reports_each_row_as_it_runs()
    {
        CustomerSetupGrammar.Reset();

        var observer = await run(Table_Grammar_Feature.Define(), "Batched data setup");

        // Two rows in the feature file → row 1 of 2, row 2 of 2, message-less, on the one step.
        var rows = observer.Progress.Where(p => p.Update.Row.HasValue).ToArray();
        rows.Select(p => p.Update.Row).ShouldBe([1, 2]);
        rows.ShouldAllBe(p => p.Update.TotalRows == 2);
        rows.ShouldAllBe(p => p.Update.Message == null);
        rows.Select(p => p.StepId).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task a_decision_table_reports_its_rows_too()
    {
        var observer = await run(Table_Grammar_Feature.Define(), "Decision table");

        observer.Progress.Select(p => p.Update.Row).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task a_throwing_Before_reports_no_rows_because_none_ran()
    {
        FailingBeforeGrammar.Reset();

        var observer = await run(Table_Grammar_Feature.Define(), "A throwing Before still runs After");

        observer.Progress.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_wait_for_step_narrates_its_poll_loop()
    {
        var observer = await run(Wait_For_Feature.Define(), "Return value converges");

        // Outstanding() answers 5 on its first two polls and 0 on the third, so the loop goes
        // around twice before converging — each non-converged attempt is a message with the
        // last value seen, and none of it is row-shaped.
        var messages = observer.Progress.Select(p => p.Update.Message).ToArray();
        messages.Length.ShouldBe(2);
        messages.ShouldAllBe(m => m != null && m.StartsWith("waiting…") && m.Contains("last value 5"));
        observer.Progress.ShouldAllBe(p => p.Update.Row == null);
    }
}
