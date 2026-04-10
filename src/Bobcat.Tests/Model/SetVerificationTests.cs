using Bobcat.Engine;
using Bobcat.Model;
using Shouldly;

namespace Bobcat.Tests.Model;

public record OrderSummary(string OrderId, string CustomerName, string Status);

public class OrderVerificationFixture : Fixture
{
    public List<OrderSummary> ActualOrders { get; } = new();

    [Then("the order summary contains")]
    [SetVerification(KeyColumns = "OrderId")]
    public IEnumerable<OrderSummary> GetOrderSummaries()
    {
        return ActualOrders;
    }

    [Then("the items list is")]
    [SetVerification] // no key columns — match by all columns
    public IEnumerable<OrderSummary> GetAllItems()
    {
        return ActualOrders;
    }
}

public class SetVerificationTests
{
    private readonly FixtureModel _model = new(typeof(OrderVerificationFixture));

    [Fact]
    public void discovers_set_verification_grammar()
    {
        var grammar = _model.FindGrammar("GetOrderSummaries");
        grammar.ShouldNotBeNull();
        grammar.ShouldBeOfType<SetVerificationGrammar>();
    }

    [Fact]
    public void key_columns_are_parsed()
    {
        var grammar = (SetVerificationGrammar)_model.FindGrammar("GetOrderSummaries")!;
        grammar.KeyColumns.ShouldBe(["OrderId"]);
    }

    [Fact]
    public async Task all_rows_match_succeeds()
    {
        var fixture = _model.CreateInstance();
        var typedFixture = (OrderVerificationFixture)fixture.Instance;
        typedFixture.ActualOrders.AddRange([
            new OrderSummary("1", "Alice", "Placed"),
            new OrderSummary("2", "Bob", "Shipped"),
        ]);

        var root = Step.ForSpecification("Verify orders");
        var step = root.Add(_model.FindGrammar("GetOrderSummaries")!);
        step.AddRow(new Dictionary<string, object>
            { ["OrderId"] = "1", ["CustomerName"] = "Alice", ["Status"] = "Placed" });
        step.AddRow(new Dictionary<string, object>
            { ["OrderId"] = "2", ["CustomerName"] = "Bob", ["Status"] = "Shipped" });

        var plan = new ExecutionPlan("sv-pass", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("sv-pass");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        context.Results.Counts.Succeeded.ShouldBeTrue();
        context.Results.Steps[0].StepStatus.ShouldBe(ResultStatus.success);
        // 6 cells: 3 columns × 2 rows, all success
        context.Results.Steps[0].Cells.Count(c => c.Status == ResultStatus.success).ShouldBe(6);
    }

    [Fact]
    public async Task wrong_value_reports_cell_failure()
    {
        var fixture = _model.CreateInstance();
        var typedFixture = (OrderVerificationFixture)fixture.Instance;
        typedFixture.ActualOrders.Add(new OrderSummary("1", "Alice", "Shipped"));

        var root = Step.ForSpecification("Wrong value");
        var step = root.Add(_model.FindGrammar("GetOrderSummaries")!);
        step.AddRow(new Dictionary<string, object>
            { ["OrderId"] = "1", ["CustomerName"] = "Alice", ["Status"] = "Placed" });

        var plan = new ExecutionPlan("sv-wrong", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("sv-wrong");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        var stepResult = context.Results.Steps[0];
        stepResult.StepStatus.ShouldBe(ResultStatus.failed);
        stepResult.FailureLevel.ShouldBe(FailureLevel.Assertion);

        // OrderId and CustomerName match, Status differs
        stepResult.Cells.First(c => c.Name == "OrderId").Status.ShouldBe(ResultStatus.success);
        stepResult.Cells.First(c => c.Name == "CustomerName").Status.ShouldBe(ResultStatus.success);

        var statusCell = stepResult.Cells.First(c => c.Name == "Status");
        statusCell.Status.ShouldBe(ResultStatus.failed);
        statusCell.DisplayText.ShouldContain("Placed");
        statusCell.DisplayText.ShouldContain("Shipped");
    }

    [Fact]
    public async Task missing_row_reported()
    {
        var fixture = _model.CreateInstance();
        // Actual has no rows — expected has one

        var root = Step.ForSpecification("Missing row");
        var step = root.Add(_model.FindGrammar("GetOrderSummaries")!);
        step.AddRow(new Dictionary<string, object>
            { ["OrderId"] = "1", ["CustomerName"] = "Alice", ["Status"] = "Placed" });

        var plan = new ExecutionPlan("sv-missing", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("sv-missing");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        var stepResult = context.Results.Steps[0];
        stepResult.StepStatus.ShouldBe(ResultStatus.failed);

        var missingCell = stepResult.Cells.First(c => c.Name == "missing-row");
        missingCell.Status.ShouldBe(ResultStatus.missing);
        missingCell.DisplayText.ShouldContain("not found");
    }

    [Fact]
    public async Task extra_row_reported()
    {
        var fixture = _model.CreateInstance();
        var typedFixture = (OrderVerificationFixture)fixture.Instance;
        typedFixture.ActualOrders.Add(new OrderSummary("1", "Alice", "Placed"));
        typedFixture.ActualOrders.Add(new OrderSummary("2", "Bob", "Shipped"));

        // Only expect row 1 — row 2 is extra
        var root = Step.ForSpecification("Extra row");
        var step = root.Add(_model.FindGrammar("GetOrderSummaries")!);
        step.AddRow(new Dictionary<string, object>
            { ["OrderId"] = "1", ["CustomerName"] = "Alice", ["Status"] = "Placed" });

        var plan = new ExecutionPlan("sv-extra", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("sv-extra");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        var stepResult = context.Results.Steps[0];
        // Extra rows don't fail — they're informational
        stepResult.StepStatus.ShouldBe(ResultStatus.success);

        var extraCell = stepResult.Cells.First(c => c.Name == "extra-row");
        extraCell.Status.ShouldBe(ResultStatus.invalid);
        extraCell.DisplayText.ShouldContain("Extra row");
        extraCell.DisplayText.ShouldContain("Bob");
    }

    [Fact]
    public async Task set_verification_is_assertion_level_failure()
    {
        var fixture = _model.CreateInstance();
        // Empty actuals, one expected — this is a failure but should NOT abort

        var root = Step.ForSpecification("Assertion level");
        var step = root.Add(_model.FindGrammar("GetOrderSummaries")!);
        step.AddRow(new Dictionary<string, object>
            { ["OrderId"] = "1", ["CustomerName"] = "Alice", ["Status"] = "Placed" });

        var plan = new ExecutionPlan("sv-assertion", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("sv-assertion");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        var stepResult = context.Results.Steps[0];
        stepResult.FailureLevel.ShouldBe(FailureLevel.Assertion);
    }

    [Fact]
    public async Task match_by_all_columns_when_no_key()
    {
        var fixture = _model.CreateInstance();
        var typedFixture = (OrderVerificationFixture)fixture.Instance;
        typedFixture.ActualOrders.Add(new OrderSummary("1", "Alice", "Placed"));

        var root = Step.ForSpecification("No key match");
        var step = root.Add(_model.FindGrammar("GetAllItems")!);
        step.AddRow(new Dictionary<string, object>
            { ["OrderId"] = "1", ["CustomerName"] = "Alice", ["Status"] = "Placed" });

        var plan = new ExecutionPlan("sv-nokey", TimeSpan.FromSeconds(30));
        root.Grammar.CreatePlan(plan, root, fixture);

        var context = new SpecExecutionContext("sv-nokey");
        var executor = new Executor([new FailureLevelContinuationRule()]);
        await executor.Execute(plan, context);

        context.Results.Counts.Succeeded.ShouldBeTrue();
    }
}
