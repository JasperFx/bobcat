using Bobcat;
using Bobcat.Engine;
using Bobcat.Model;
using Bobcat.Rendering;

// --- Demo fixture ---
var model = new FixtureModel(typeof(CalculatorFixture));

// --- Passing scenario ---
{
    var fixture = model.CreateInstance();
    var root = Step.ForSpecification("Add two numbers (passing)");
    root.Add(model.FindGrammar("TheLeftOperandIs")!).WithValue("value", 25);
    root.Add(model.FindGrammar("TheRightOperandIs")!).WithValue("value", 50);
    root.Add(model.FindGrammar("TheOperandsAreAdded")!);
    root.Add(model.FindGrammar("TheResultShouldBe")!).WithValue("expected", 75);
    root.Add(model.FindGrammar("TheResultIsPositive")!);

    var plan = new ExecutionPlan("add-passing", TimeSpan.FromSeconds(30));
    root.Grammar.CreatePlan(plan, root, fixture);

    var context = new SpecExecutionContext("add-passing");
    var executor = new Executor([new FailureLevelContinuationRule()]);
    await executor.Execute(plan, context);

    var renderer = new CommandLineRenderer();
    renderer.RenderResults("Add two numbers (passing)", context.Results);
}

// --- Failing scenario ---
{
    var fixture = model.CreateInstance();
    var root = Step.ForSpecification("Add two numbers (failing)");
    root.Add(model.FindGrammar("TheLeftOperandIs")!).WithValue("value", 25);
    root.Add(model.FindGrammar("TheRightOperandIs")!).WithValue("value", 50);
    root.Add(model.FindGrammar("TheOperandsAreAdded")!);
    root.Add(model.FindGrammar("TheResultShouldBe")!).WithValue("expected", 99);
    root.Add(model.FindGrammar("TheResultIsPositive")!);

    var plan = new ExecutionPlan("add-failing", TimeSpan.FromSeconds(30));
    root.Grammar.CreatePlan(plan, root, fixture);

    var context = new SpecExecutionContext("add-failing");
    var executor = new Executor([new FailureLevelContinuationRule()]);
    await executor.Execute(plan, context);

    var renderer = new CommandLineRenderer();
    renderer.RenderResults("Add two numbers (failing)", context.Results);
}

// --- Assertion failure continues scenario ---
{
    var fixture = model.CreateInstance();
    var root = Step.ForSpecification("Negative result");
    root.Add(model.FindGrammar("TheLeftOperandIs")!).WithValue("value", -10);
    root.Add(model.FindGrammar("TheRightOperandIs")!).WithValue("value", 3);
    root.Add(model.FindGrammar("TheOperandsAreAdded")!);
    root.Add(model.FindGrammar("TheResultIsPositive")!); // fails (assertion)
    root.Add(model.FindGrammar("TheResultShouldBe")!).WithValue("expected", -7); // still runs

    var plan = new ExecutionPlan("negative-result", TimeSpan.FromSeconds(30));
    root.Grammar.CreatePlan(plan, root, fixture);

    var context = new SpecExecutionContext("negative-result");
    var executor = new Executor([new FailureLevelContinuationRule()]);
    await executor.Execute(plan, context);

    var renderer = new CommandLineRenderer();
    renderer.RenderResults("Negative result (assertion continues)", context.Results);
}

// --- Demo fixture definition ---
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
