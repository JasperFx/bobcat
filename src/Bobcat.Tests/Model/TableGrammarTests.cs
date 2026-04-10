using Bobcat.Engine;
using Bobcat.Model;
using Shouldly;

namespace Bobcat.Tests.Model;

public class OrderSetupFixture : Fixture
{
    public List<(string Name, decimal Amount)> Orders { get; } = new();

    [Given("the following orders")]
    [Table]
    public void SetUpOrder(string name, decimal amount)
    {
        Orders.Add((name, amount));
    }
}

public class TableGrammarTests
{
    private readonly FixtureModel _model = new(typeof(OrderSetupFixture));

    [Fact]
    public void discovers_table_grammar()
    {
        var grammar = _model.FindGrammar("SetUpOrder");
        grammar.ShouldNotBeNull();
        grammar.ShouldBeOfType<TableGrammar>();
    }

    [Fact]
    public async Task table_creates_one_step_per_row()
    {
        var fixture = _model.CreateInstance();
        var root = Step.ForSpecification("Order setup");
        var step = root.Add(_model.FindGrammar("SetUpOrder")!);
        step.AddRow(new Dictionary<string, object> { ["name"] = "Alice", ["amount"] = 100m });
        step.AddRow(new Dictionary<string, object> { ["name"] = "Bob", ["amount"] = 200m });
        step.AddRow(new Dictionary<string, object> { ["name"] = "Carol", ["amount"] = 300m });

        var plan = new ExecutionPlan("table-test", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        plan.Steps.Count.ShouldBe(3);
        plan.Steps[0].StepId.ShouldBe("SetUpOrder.row1");
        plan.Steps[1].StepId.ShouldBe("SetUpOrder.row2");
        plan.Steps[2].StepId.ShouldBe("SetUpOrder.row3");
    }

    [Fact]
    public async Task table_executes_each_row()
    {
        var fixture = _model.CreateInstance();
        var root = Step.ForSpecification("Order setup");
        var step = root.Add(_model.FindGrammar("SetUpOrder")!);
        step.AddRow(new Dictionary<string, object> { ["name"] = "Alice", ["amount"] = 100m });
        step.AddRow(new Dictionary<string, object> { ["name"] = "Bob", ["amount"] = 200m });

        var plan = new ExecutionPlan("table-exec", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("table-exec");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        var typedFixture = (OrderSetupFixture)fixture.Instance;
        typedFixture.Orders.Count.ShouldBe(2);
        typedFixture.Orders[0].ShouldBe(("Alice", 100m));
        typedFixture.Orders[1].ShouldBe(("Bob", 200m));

        context.Results.Counts.Succeeded.ShouldBeTrue();
        context.Results.Counts.Rights.ShouldBe(2);
    }

    [Fact]
    public async Task table_with_no_rows_creates_no_steps()
    {
        var fixture = _model.CreateInstance();
        var root = Step.ForSpecification("Empty table");
        root.Add(_model.FindGrammar("SetUpOrder")!);
        // No rows added

        var plan = new ExecutionPlan("empty-table", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        plan.Steps.Count.ShouldBe(0);
    }

    [Fact]
    public void step_rows_are_stored_and_retrievable()
    {
        var root = Step.ForSpecification("test");
        var step = root.Add(_model.FindGrammar("SetUpOrder")!);
        step.AddRow(new Dictionary<string, object> { ["name"] = "Alice" });
        step.AddRow(new Dictionary<string, object> { ["name"] = "Bob" });

        step.Rows.Count.ShouldBe(2);
        step.Rows[0]["name"].ShouldBe("Alice");
        step.Rows[1]["name"].ShouldBe("Bob");
    }
}
