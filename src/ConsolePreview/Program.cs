using Bobcat;
using Bobcat.Engine;
using Bobcat.Model;
using Bobcat.Rendering;

var renderer = new CommandLineRenderer();

// --- 1. Passing scenario (Sentence grammars) ---
{
    var model = new FixtureModel(typeof(CalculatorFixture));
    var fixture = model.CreateInstance();
    var root = Step.ForSpecification("Add two numbers");
    root.Add(model.FindGrammar("TheLeftOperandIs")!).WithValue("value", 25);
    root.Add(model.FindGrammar("TheRightOperandIs")!).WithValue("value", 50);
    root.Add(model.FindGrammar("TheOperandsAreAdded")!);
    root.Add(model.FindGrammar("TheResultShouldBe")!).WithValue("expected", 75);
    root.Add(model.FindGrammar("TheResultIsPositive")!);

    var plan = new ExecutionPlan("calc-pass", TimeSpan.FromSeconds(30));
    root.Grammar.CreatePlan(plan, root, fixture);

    var context = new SpecExecutionContext("calc-pass");
    await new Executor([new FailureLevelContinuationRule()]).Execute(plan, context);
    renderer.RenderResults("Calculator: 25 + 50 = 75 (passing)", context.Results);
}

// --- 2. Table grammar demo ---
{
    var model = new FixtureModel(typeof(OrderFixture));
    var fixture = model.CreateInstance();
    var root = Step.ForSpecification("Set up orders via table");
    var step = root.Add(model.FindGrammar("SetUpOrder")!);
    step.AddRow(new Dictionary<string, object> { ["name"] = "Alice", ["amount"] = 100m });
    step.AddRow(new Dictionary<string, object> { ["name"] = "Bob", ["amount"] = 200m });
    step.AddRow(new Dictionary<string, object> { ["name"] = "Carol", ["amount"] = 300m });

    var plan = new ExecutionPlan("table-demo", TimeSpan.FromSeconds(30));
    root.Grammar.CreatePlan(plan, root, fixture);

    var context = new SpecExecutionContext("table-demo");
    await new Executor([new FailureLevelContinuationRule()]).Execute(plan, context);
    renderer.RenderResults("Table Grammar: Order Setup (3 rows)", context.Results);
}

// --- 3. Set verification — all match ---
{
    var model = new FixtureModel(typeof(OrderFixture));
    var fixture = model.CreateInstance();
    var typed = (OrderFixture)fixture.Instance;
    typed.Orders.AddRange([
        new OrderRow("ORD-1", "Alice", "Placed"),
        new OrderRow("ORD-2", "Bob", "Shipped"),
    ]);

    var root = Step.ForSpecification("Set verification: all match");
    var sv = root.Add(model.FindGrammar("GetOrders")!);
    sv.AddRow(new Dictionary<string, object>
        { ["OrderId"] = "ORD-1", ["CustomerName"] = "Alice", ["Status"] = "Placed" });
    sv.AddRow(new Dictionary<string, object>
        { ["OrderId"] = "ORD-2", ["CustomerName"] = "Bob", ["Status"] = "Shipped" });

    var plan = new ExecutionPlan("sv-pass", TimeSpan.FromSeconds(30));
    root.Grammar.CreatePlan(plan, root, fixture);

    var context = new SpecExecutionContext("sv-pass");
    await new Executor([new FailureLevelContinuationRule()]).Execute(plan, context);
    renderer.RenderResults("Set Verification: All Match", context.Results);
}

// --- 4. Set verification — wrong value + extra row ---
{
    var model = new FixtureModel(typeof(OrderFixture));
    var fixture = model.CreateInstance();
    var typed = (OrderFixture)fixture.Instance;
    typed.Orders.AddRange([
        new OrderRow("ORD-1", "Alice", "Cancelled"),  // Status differs from expected
        new OrderRow("ORD-2", "Bob", "Shipped"),
        new OrderRow("ORD-3", "Carol", "Placed"),      // Extra — not in expected
    ]);

    var root = Step.ForSpecification("Set verification: diffs");
    var sv = root.Add(model.FindGrammar("GetOrders")!);
    sv.AddRow(new Dictionary<string, object>
        { ["OrderId"] = "ORD-1", ["CustomerName"] = "Alice", ["Status"] = "Placed" });
    sv.AddRow(new Dictionary<string, object>
        { ["OrderId"] = "ORD-2", ["CustomerName"] = "Bob", ["Status"] = "Shipped" });
    sv.AddRow(new Dictionary<string, object>
        { ["OrderId"] = "ORD-99", ["CustomerName"] = "Dave", ["Status"] = "Pending" });

    var plan = new ExecutionPlan("sv-diffs", TimeSpan.FromSeconds(30));
    root.Grammar.CreatePlan(plan, root, fixture);

    var context = new SpecExecutionContext("sv-diffs");
    await new Executor([new FailureLevelContinuationRule()]).Execute(plan, context);
    renderer.RenderResults("Set Verification: Wrong Value + Missing + Extra", context.Results);
}

// --- Fixture definitions ---

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
        if (Result != expected) throw new Exception($"Expected {expected} but got {Result}");
    }

    [Check("the result is positive")]
    public bool TheResultIsPositive() => Result > 0;
}

public record OrderRow(string OrderId, string CustomerName, string Status);

public class OrderFixture : Fixture
{
    public List<OrderRow> Orders { get; } = new();

    [Given("the following orders")]
    [Table]
    public void SetUpOrder(string name, decimal amount)
    {
        // In a real test this would insert into a database
    }

    [Then("the orders are")]
    [SetVerification(KeyColumns = "OrderId")]
    public IEnumerable<OrderRow> GetOrders() => Orders;
}
