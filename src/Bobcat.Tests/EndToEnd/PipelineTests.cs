using Bobcat.Engine;
using Bobcat.Model;
using Shouldly;

namespace Bobcat.Tests.EndToEnd;

// A sample fixture for testing the pipeline
public class CalculatorFixture : Fixture
{
    public int Left { get; set; }
    public int Right { get; set; }
    public int Result { get; set; }

    [Given("the left operand is {int}")]
    public void TheLeftOperandIs(int value) => Left = value;

    [Given("the right operand is {int}")]
    public void TheRightOperandIs(int value) => Right = value;

    [When("the operands are added")]
    public void TheOperandsAreAdded() => Result = Left + Right;

    [Then("the result should be {int}")]
    public void TheResultShouldBe(int expected)
    {
        if (Result != expected)
            throw new Exception($"Expected {expected} but got {Result}");
    }

    [Check("the result is positive")]
    public bool TheResultIsPositive() => Result > 0;
}

public class PipelineTests
{
    private readonly FixtureModel _fixtureModel = new(typeof(CalculatorFixture));

    [Fact]
    public void discovers_grammars_from_fixture()
    {
        _fixtureModel.Grammars.ShouldContainKey("TheLeftOperandIs");
        _fixtureModel.Grammars.ShouldContainKey("TheRightOperandIs");
        _fixtureModel.Grammars.ShouldContainKey("TheOperandsAreAdded");
        _fixtureModel.Grammars.ShouldContainKey("TheResultShouldBe");
        _fixtureModel.Grammars.ShouldContainKey("TheResultIsPositive");
    }

    [Fact]
    public void step_add_generates_correct_ids()
    {
        var root = Step.ForSpecification("Add two numbers");
        var given1 = root.Add(_fixtureModel.FindGrammar("TheLeftOperandIs")!);
        var given2 = root.Add(_fixtureModel.FindGrammar("TheRightOperandIs")!);
        var when = root.Add(_fixtureModel.FindGrammar("TheOperandsAreAdded")!);
        var then = root.Add(_fixtureModel.FindGrammar("TheResultShouldBe")!);

        given1.Id.ShouldBe("TheLeftOperandIs");
        given2.Id.ShouldBe("TheRightOperandIs");
        when.Id.ShouldBe("TheOperandsAreAdded");
        then.Id.ShouldBe("TheResultShouldBe");
    }

    [Fact]
    public void step_add_deduplicates_same_grammar()
    {
        var root = Step.ForSpecification("Duplicate test");
        var first = root.Add(_fixtureModel.FindGrammar("TheLeftOperandIs")!);
        var second = root.Add(_fixtureModel.FindGrammar("TheLeftOperandIs")!);

        first.Id.ShouldBe("TheLeftOperandIs");
        second.Id.ShouldBe("TheLeftOperandIs.2");
    }

    [Fact]
    public async Task full_pipeline_passing_scenario()
    {
        var fixture = _fixtureModel.CreateInstance();

        // Build the step tree
        var root = Step.ForSpecification("Add 2 + 3");
        root.Add(_fixtureModel.FindGrammar("TheLeftOperandIs")!).WithValue("value", 2);
        root.Add(_fixtureModel.FindGrammar("TheRightOperandIs")!).WithValue("value", 3);
        root.Add(_fixtureModel.FindGrammar("TheOperandsAreAdded")!);
        root.Add(_fixtureModel.FindGrammar("TheResultShouldBe")!).WithValue("expected", 5);

        // Create the execution plan
        var plan = new ExecutionPlan("add-2-3", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        plan.Steps.Count.ShouldBe(4);

        // Execute
        var context = new SpecExecutionContext("add-2-3");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        // Verify results
        var results = context.Results;
        results.Counts.Rights.ShouldBe(4);
        results.Counts.Wrongs.ShouldBe(0);
        results.Counts.Errors.ShouldBe(0);
        results.Counts.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task full_pipeline_with_assertion_failure_continues()
    {
        var fixture = _fixtureModel.CreateInstance();

        // Build a step tree where the assertion will fail (2 + 3 != 99)
        var root = Step.ForSpecification("Wrong answer");
        root.Add(_fixtureModel.FindGrammar("TheLeftOperandIs")!).WithValue("value", 2);
        root.Add(_fixtureModel.FindGrammar("TheRightOperandIs")!).WithValue("value", 3);
        root.Add(_fixtureModel.FindGrammar("TheOperandsAreAdded")!);
        root.Add(_fixtureModel.FindGrammar("TheResultShouldBe")!).WithValue("expected", 99);
        // This check should still run after the failure above
        root.Add(_fixtureModel.FindGrammar("TheResultIsPositive")!);

        var plan = new ExecutionPlan("wrong-answer", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("wrong-answer");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        var results = context.Results;

        // The Then step threw an exception — classified as Critical,
        // so it aborted and the Check step should NOT have run.
        results.Steps.Count.ShouldBe(4);

        // Given steps succeed
        results.Steps[0].StepStatus.ShouldBe(ResultStatus.success);
        results.Steps[1].StepStatus.ShouldBe(ResultStatus.success);
        // When step succeeds
        results.Steps[2].StepStatus.ShouldBe(ResultStatus.success);
        // Then step errored (threw exception) — Critical, aborts scenario
        results.Steps[3].StepStatus.ShouldBe(ResultStatus.error);
        results.Steps[3].FailureLevel.ShouldBe(FailureLevel.Critical);
    }

    [Fact]
    public async Task fact_check_passing()
    {
        var fixture = _fixtureModel.CreateInstance();

        var root = Step.ForSpecification("Positive check");
        root.Add(_fixtureModel.FindGrammar("TheLeftOperandIs")!).WithValue("value", 5);
        root.Add(_fixtureModel.FindGrammar("TheRightOperandIs")!).WithValue("value", 3);
        root.Add(_fixtureModel.FindGrammar("TheOperandsAreAdded")!);
        root.Add(_fixtureModel.FindGrammar("TheResultIsPositive")!);

        var plan = new ExecutionPlan("positive-check", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("positive-check");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        var results = context.Results;
        results.Counts.Succeeded.ShouldBeTrue();
        results.Steps.Last().StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task fact_check_failing()
    {
        var fixture = _fixtureModel.CreateInstance();

        var root = Step.ForSpecification("Negative check");
        root.Add(_fixtureModel.FindGrammar("TheLeftOperandIs")!).WithValue("value", -5);
        root.Add(_fixtureModel.FindGrammar("TheRightOperandIs")!).WithValue("value", 3);
        root.Add(_fixtureModel.FindGrammar("TheOperandsAreAdded")!);
        root.Add(_fixtureModel.FindGrammar("TheResultIsPositive")!);

        var plan = new ExecutionPlan("negative-check", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("negative-check");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        var results = context.Results;
        // The Check returns false → assertion failure, which should NOT abort
        results.Steps.Last().StepStatus.ShouldBe(ResultStatus.failed);
        results.Steps.Last().FailureLevel.ShouldBe(FailureLevel.Assertion);
        results.Counts.Wrongs.ShouldBe(1);
    }

    [Fact]
    public async Task assertion_failure_continues_to_next_step()
    {
        var fixture = _fixtureModel.CreateInstance();

        // -5 + 3 = -2 → "is positive" fails but "result should be -2" passes
        var root = Step.ForSpecification("Continue after assertion failure");
        root.Add(_fixtureModel.FindGrammar("TheLeftOperandIs")!).WithValue("value", -5);
        root.Add(_fixtureModel.FindGrammar("TheRightOperandIs")!).WithValue("value", 3);
        root.Add(_fixtureModel.FindGrammar("TheOperandsAreAdded")!);
        root.Add(_fixtureModel.FindGrammar("TheResultIsPositive")!); // fails (assertion)
        root.Add(_fixtureModel.FindGrammar("TheResultShouldBe")!).WithValue("expected", -2); // should still run

        var plan = new ExecutionPlan("continue-after-fail", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("continue-after-fail");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        var results = context.Results;
        // All 5 steps should have executed — assertion failure doesn't abort
        results.Steps.Count.ShouldBe(5);
        results.Steps[3].StepStatus.ShouldBe(ResultStatus.failed); // Check failed
        results.Steps[3].FailureLevel.ShouldBe(FailureLevel.Assertion);
        results.Steps[4].StepStatus.ShouldBe(ResultStatus.success); // Then still ran and passed
        results.Counts.Rights.ShouldBe(4);
        results.Counts.Wrongs.ShouldBe(1);
    }

    [Fact]
    public async Task critical_exception_aborts_scenario()
    {
        var fixture = _fixtureModel.CreateInstance();

        // Don't set up operands — the When step will succeed but result in 0
        // Instead, throw a SpecCriticalException from a Given step
        var root = Step.ForSpecification("Critical failure");
        root.Add(new CriticalGrammar());
        root.Add(_fixtureModel.FindGrammar("TheResultIsPositive")!);

        var plan = new ExecutionPlan("critical", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("critical");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        // Only the first step should have executed
        context.Results.Steps.Count.ShouldBe(1);
        context.Results.Steps[0].FailureLevel.ShouldBe(FailureLevel.Critical);
    }

    [Fact]
    public async Task catastrophic_exception_stops_suite()
    {
        var fixture = _fixtureModel.CreateInstance();

        var root = Step.ForSpecification("Catastrophic failure");
        root.Add(new CatastrophicGrammar());
        root.Add(_fixtureModel.FindGrammar("TheResultIsPositive")!);

        var plan = new ExecutionPlan("catastrophic", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("catastrophic");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        context.Results.Steps.Count.ShouldBe(1);
        context.Results.Steps[0].FailureLevel.ShouldBe(FailureLevel.Catastrophic);
    }
}

// Helper grammars for testing failure levels
internal class CriticalGrammar : IGrammar
{
    public string Name => "CriticalStep";

    public void CreatePlan(ExecutionPlan plan, Step step, FixtureInstance fixture)
    {
        plan.Add(new CriticalStep(step.Id));
    }

    private class CriticalStep : IExecutionStep
    {
        public CriticalStep(string stepId) => StepId = stepId;
        public string StepId { get; }
        public StepKind StepKind => StepKind.Given;

        public Task Execute(IStepContext context, StepResult result, CancellationToken token)
            => throw new SpecCriticalException("Database connection lost");
    }
}

internal class CatastrophicGrammar : IGrammar
{
    public string Name => "CatastrophicStep";

    public void CreatePlan(ExecutionPlan plan, Step step, FixtureInstance fixture)
    {
        plan.Add(new CatastrophicStep(step.Id));
    }

    private class CatastrophicStep : IExecutionStep
    {
        public CatastrophicStep(string stepId) => StepId = stepId;
        public string StepId { get; }
        public StepKind StepKind => StepKind.SetUp;

        public Task Execute(IStepContext context, StepResult result, CancellationToken token)
            => throw new SpecCatastrophicException("Host failed to start");
    }
}
