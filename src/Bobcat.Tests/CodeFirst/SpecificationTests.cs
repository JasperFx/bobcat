using Bobcat.CodeFirst;
using Bobcat.Engine;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.CodeFirst;

/// <summary>
/// The code-first authoring API end to end through <see cref="BobcatRunner"/>: discovery, the
/// compose-then-execute model, and the failure semantics that differ from Gherkin on purpose.
/// </summary>
public class SpecificationTests
{
    private static async Task<SuiteResults> run<T>() where T : Specification, new()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddSpecification<T>();
        return await runner.RunAll();
    }

    private static ScenarioResult scenario(SuiteResults results, string title)
        => results.AllScenarios.Single(s => s.Title == title);

    // --- discovery -----------------------------------------------------------------------------

    [Fact]
    public void feature_title_is_derived_from_the_class_name_minus_a_conventional_suffix()
    {
        SpecificationFeature.DeriveTitle(typeof(OrderSagaSpecs)).ShouldBe("Order Saga");
        SpecificationFeature.DeriveTitle(typeof(BankAccountSpecification)).ShouldBe("Bank Account");
        SpecificationFeature.DeriveTitle(typeof(TitledSpecs)).ShouldBe("A Better Name");
    }

    [Fact]
    public void scenario_titles_come_from_the_attribute_or_the_method_name()
    {
        var feature = SpecificationFeature.Build<OrderSagaSpecs>();

        feature.Scenarios.Select(s => s.Title).ShouldBe(
        [
            "starting an order",
            "Completing An Order",
            "An order times out"
        ]);
    }

    [Fact]
    public void tags_flow_onto_the_scenario_definition()
    {
        var feature = SpecificationFeature.Build<OrderSagaSpecs>();

        feature.Scenarios.Single(s => s.Title == "An order times out").Tags.ShouldBe(["retry(2)", "slow"]);
        feature.Scenarios.Single(s => s.Title == "starting an order").Tags.ShouldBeEmpty();
    }

    [Fact]
    public void the_fixture_type_is_the_specification_itself()
    {
        SpecificationFeature.Build<OrderSagaSpecs>().FixtureType.ShouldBe(typeof(OrderSagaSpecs));
    }

    [Fact]
    public void a_scenario_method_with_parameters_is_a_configuration_error()
    {
        Should.Throw<BobcatConfigurationException>(() => SpecificationFeature.Build(typeof(BadShapeSpecs)))
            .Message.ShouldContain("must be 'void' with no parameters");
    }

    [Fact]
    public void a_specification_without_scenarios_is_a_configuration_error()
    {
        Should.Throw<BobcatConfigurationException>(() => SpecificationFeature.Build(typeof(EmptySpecs)))
            .Message.ShouldContain("no [Scenario] methods");
    }

    [Fact]
    public void scan_registers_every_concrete_specification_in_the_assembly()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.ScanForSpecifications(typeof(SpecificationTests).Assembly);

        runner.Features.Select(f => f.FixtureType).ShouldContain(typeof(OrderSagaSpecs));
        runner.Features.Select(f => f.FixtureType).ShouldNotContain(typeof(AbstractSpecs));
        // No public parameterless constructor means "not a runnable specification" to the scan, which
        // is what keeps the deliberately misconfigured fixtures below out of it.
        runner.Features.Select(f => f.FixtureType).ShouldNotContain(typeof(BadShapeSpecs));
    }

    // --- execution -----------------------------------------------------------------------------

    [Fact]
    public async Task steps_execute_in_declaration_order_with_their_kinds_and_text()
    {
        var results = await run<ArithmeticSpecs>();
        var addition = scenario(results, "addition works");

        addition.Results.Counts.Succeeded.ShouldBeTrue();
        addition.Results.Steps.Select(s => (s.StepKind, s.StepText)).ShouldBe(
        [
            (StepKind.Given, "the left operand is 2"),
            (StepKind.Given, "the right operand is 2"),
            (StepKind.When, "they are added"),
            (StepKind.Then, "the sum should be 4")
        ]);
    }

    [Fact]
    public async Task a_captured_value_is_readable_from_a_later_step()
    {
        var results = await run<ArithmeticSpecs>();
        var step = scenario(results, "addition works").Results.Steps.Last();

        step.StepStatus.ShouldBe(ResultStatus.success);
        var cell = step.Cells.Single();
        cell.Name.ShouldBe("result");
        cell.Expected.ShouldBe("4");
        cell.Actual.ShouldBe("4");
    }

    [Fact]
    public async Task a_value_expectation_that_disagrees_fails_the_step_with_expected_and_actual()
    {
        var results = await run<ArithmeticSpecs>();
        var step = scenario(results, "subtraction disagrees").Results.Steps.Last();

        step.StepText.ShouldBe("the difference should be 4");
        step.StepStatus.ShouldBe(ResultStatus.failed);
        step.FailureLevel.ShouldBe(FailureLevel.Assertion);
        var cell = step.Cells.Single();
        cell.Expected.ShouldBe("4");
        cell.Actual.ShouldBe("5");
        cell.DisplayText.ShouldBe("expected '4', got '5'");
    }

    [Fact]
    public async Task a_then_body_that_throws_is_an_assertion_failure_and_the_scenario_continues()
    {
        var results = await run<ArithmeticSpecs>();
        var failing = scenario(results, "a throwing assertion does not stop the scenario");

        var steps = failing.Results.Steps;
        steps.Count.ShouldBe(3, "the step after the throwing Then must still run");

        var thrower = steps[1];
        thrower.StepStatus.ShouldBe(ResultStatus.failed);
        thrower.FailureLevel.ShouldBe(FailureLevel.Assertion);
        thrower.Exception.ShouldBeNull("the exception is on the cell, not the step, so it reads as failed rather than errored");
        thrower.Cells.Single().Name.ShouldBe("assertion");
        thrower.Cells.Single().DisplayText.ShouldBe("the assertion library said no");
        thrower.Cells.Single().Exception.ShouldBeOfType<InvalidOperationException>();

        steps[2].StepStatus.ShouldBe(ResultStatus.success);
        failing.Outcome.ShouldBe(RunOutcome.Failed);
    }

    [Fact]
    public async Task a_when_that_throws_is_critical_and_stops_the_scenario()
    {
        var results = await run<ArithmeticSpecs>();
        var exploding = scenario(results, "division explodes");

        exploding.Results.Steps.Count.ShouldBe(1, "nothing after the critical step runs");
        exploding.Results.Steps[0].StepStatus.ShouldBe(ResultStatus.error);
        exploding.Results.Steps[0].FailureLevel.ShouldBe(FailureLevel.Critical);
        exploding.Results.Steps[0].Exception.ShouldBeOfType<DivideByZeroException>();
    }

    [Fact]
    public async Task a_check_that_returns_false_fails_the_step_and_the_next_step_still_runs()
    {
        var results = await run<ArithmeticSpecs>();
        var checks = scenario(results, "checks gather").Results.Steps;

        checks.Count.ShouldBe(2);
        checks[0].StepStatus.ShouldBe(ResultStatus.failed);
        checks[1].StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task a_caller_argument_expression_becomes_readable_step_text()
    {
        var results = await run<ArithmeticSpecs>();
        var steps = scenario(results, "expression text").Results.Steps;

        steps[0].StepText.ShouldBe("_left + _right should be 7");
        steps[1].StepText.ShouldBe("Task.FromResult(_left * _right) should be 12");
    }

    [Fact]
    public async Task reading_a_captured_value_during_composition_fails_that_scenario_and_names_the_problem()
    {
        var results = await run<TooEagerSpecs>();

        var scenario = results.AllScenarios.Single();
        scenario.Results.Counts.Succeeded.ShouldBeFalse();
        var step = scenario.Results.Steps.Single();
        step.StepText.ShouldBe("composing the scenario");
        step.Exception.ShouldBeOfType<InvalidOperationException>()
            .Message.ShouldContain("has no value yet");
    }

    [Fact]
    public void declaring_a_step_outside_a_scenario_method_is_rejected_with_an_explanation()
    {
        Should.Throw<InvalidOperationException>(() => new ArithmeticSpecs().DeclareOutsideComposition())
            .Message.ShouldContain("not composing a scenario");
    }

    // --- tables ---------------------------------------------------------------------------------

    [Fact]
    public async Task with_rows_renders_the_step_input_as_a_table_of_ok_cells()
    {
        var results = await run<TableSpecs>();
        var given = scenario(results, "rows render").Results.Steps[0];

        given.StepStatus.ShouldBe(ResultStatus.success);
        given.IsSetVerification.ShouldBeTrue();
        // Marker records have no properties of their own, so the type column carries the name.
        given.SetVerificationColumns.ShouldBe([RowTable.TypeColumn, "Amount"]);
        given.Cells.Count.ShouldBe(4);
        given.Cells.All(c => c.Status == ResultStatus.ok).ShouldBeTrue();
        given.Cells.Where(c => c.Name == RowTable.TypeColumn).Select(c => c.Actual).ShouldBe(["Opened", "Deposited"]);
        given.Cells.Where(c => c.Name == "Amount").Select(c => c.Actual).ShouldBe(["", "50"]);
    }

    [Fact]
    public async Task then_rows_passes_when_the_set_matches()
    {
        var results = await run<TableSpecs>();
        var step = scenario(results, "rows render").Results.Steps[1];

        step.StepText.ShouldBe("the ledger should be");
        step.StepStatus.ShouldBe(ResultStatus.success);
        step.IsSetVerification.ShouldBeTrue();
        step.SetVerificationColumns.ShouldBe(["Id", "Amount"]);
        step.Cells.All(c => c.Status == ResultStatus.success).ShouldBeTrue();
    }

    [Fact]
    public async Task then_rows_reports_a_wrong_cell_and_a_missing_row()
    {
        var results = await run<TableSpecs>();
        var step = scenario(results, "rows disagree").Results.Steps[0];

        step.StepStatus.ShouldBe(ResultStatus.failed);
        // Keyed by Id, so the second row is found with a wrong Amount rather than reported missing.
        step.Cells.ShouldContain(c => c.Name == "Amount" && c.Status == ResultStatus.failed && c.Expected == "99" && c.Actual == "50");
        step.Cells.ShouldContain(c => c.Name == "missing-row");
    }

    [Fact]
    public async Task then_rows_should_be_empty_lists_the_intruders()
    {
        var results = await run<TableSpecs>();
        var step = scenario(results, "rows disagree").Results.Steps[1];

        step.StepText.ShouldBe("the ledger should be empty");
        step.StepStatus.ShouldBe(ResultStatus.failed);
        step.Cells.Count(c => c.Name == "extra-row").ShouldBe(2);
    }

    // --- lifecycle -----------------------------------------------------------------------------

    [Fact]
    public async Task hooks_are_discovered_by_name_and_see_the_context()
    {
        LifecycleSpecs.Log.Clear();
        var results = await run<LifecycleSpecs>();

        results.Counts.Succeeded.ShouldBeTrue();
        LifecycleSpecs.Log.ShouldBe(
        [
            "BeforeAll",
            "BeforeEach:first", "step:first", "AfterEach:first",
            "BeforeEach:second", "step:second", "AfterEach:second",
            "AfterAll"
        ]);
    }

    [Fact]
    public async Task a_hosted_fixture_sees_the_same_context_as_the_specification()
    {
        var results = await run<HostingSpecs>();
        results.Counts.Succeeded.ShouldBeTrue();
    }

    // --- fixtures ------------------------------------------------------------------------------

    public class OrderSagaSpecs : Specification
    {
        [Scenario] public void starting_an_order() => Given("an order", () => { });
        [Scenario] public void CompletingAnOrder() => Given("an order", () => { });
        [Scenario("An order times out", Tags = ["retry(2)", "slow"])] public void timeout() => Given("an order", () => { });
    }

    public class BankAccountSpecification : Specification
    {
        [Scenario] public void opens() => Given("an account", () => { });
    }

    [FixtureTitle("A Better Name")]
    public class TitledSpecs : Specification
    {
        [Scenario] public void x() => Given("y", () => { });
    }

    public abstract class AbstractSpecs : Specification
    {
        [Scenario] public void x() => Given("y", () => { });
    }

    public class BadShapeSpecs : Specification
    {
        // No parameterless constructor, so the assembly scan leaves it alone.
        public BadShapeSpecs(int _) { }
        [Scenario] public void takes_an_argument(int n) => Given("y", () => { });
    }

    public class EmptySpecs : Specification
    {
        public EmptySpecs(int _) { }
    }

    public class ArithmeticSpecs : Specification
    {
        private int _left;
        private int _right;

        [Scenario]
        public void addition_works()
        {
            Given("the left operand is 2", () => _left = 2);
            Given("the right operand is 2", () => _right = 2);
            var sum = When("they are added", () => _left + _right);
            Then("the sum", () => sum.Value).ShouldBe(4);
        }

        [Scenario]
        public void subtraction_disagrees()
        {
            Given("the left operand is 9", () => _left = 9);
            Given("the right operand is 4", () => _right = 4);
            Then("the difference", () => _left - _right).ShouldBe(4);
        }

        [Scenario]
        public void a_throwing_assertion_does_not_stop_the_scenario()
        {
            Given("anything", () => { });
            Then("something the assertion library dislikes", () => throw new InvalidOperationException("the assertion library said no\nwith more detail"));
            Then("a later assertion", () => { });
        }

        [Scenario]
        public void division_explodes()
        {
            When("dividing by zero", () => throw new DivideByZeroException("attempted to divide by zero"));
            Then("never reached", () => { });
        }

        [Scenario]
        public void checks_gather()
        {
            Check("one is two", () => 1 == 2);
            Check("two is two", () => Task.FromResult(2 == 2));
        }

        [Scenario]
        public void expression_text()
        {
            _left = 3;
            _right = 4;
            Then(() => _left + _right).ShouldBe(7);
            Then(() => Task.FromResult(_left * _right)).ShouldBe(12);
        }

        public void DeclareOutsideComposition() => Given("too early", () => { });
    }

    public class TooEagerSpecs : Specification
    {
        [Scenario]
        public void reads_a_capture_too_soon()
        {
            var value = Given("a value", () => 1);
            // Reading .Value here, during composition, is the mistake.
            Then("the value", () => value.Value + 0).ShouldBe(value.Value);
        }
    }

    public record Opened;
    public record Deposited(decimal Amount);
    public record LedgerLine(int Id, decimal Amount);

    public class TableSpecs : Specification
    {
        private readonly List<LedgerLine> _ledger = new();

        [Scenario]
        public void rows_render()
        {
            Given("these events", () => _ledger.AddRange([new LedgerLine(1, 10m), new LedgerLine(2, 50m)]))
                .WithRows(new Opened(), new Deposited(50m));

            ThenRows("the ledger", () => _ledger).KeyedBy("Id")
                .ShouldMatch(new { Id = 1, Amount = 10m }, new { Id = 2, Amount = 50m });
        }

        [Scenario]
        public void rows_disagree()
        {
            _ledger.AddRange([new LedgerLine(1, 10m), new LedgerLine(2, 50m)]);

            ThenRows("the ledger", () => Task.FromResult(_ledger)).KeyedBy("Id")
                .ShouldMatch(new { Id = 1, Amount = 10m }, new { Id = 2, Amount = 99m }, new { Id = 3, Amount = 1m });

            ThenRows("the ledger", () => _ledger).ShouldBeEmpty();
        }
    }

    public class LifecycleSpecs : Specification
    {
        public static readonly List<string> Log = new();

        public static void BeforeAll(IStepContext context) => Log.Add("BeforeAll");
        public static Task AfterAllAsync() { Log.Add("AfterAll"); return Task.CompletedTask; }

        public void BeforeEach(IStepContext context)
        {
            context.ShouldNotBeNull();
            Context.ShouldBeSameAs(context);
            Log.Add($"BeforeEach:{context.SpecId}");
        }

        public Task AfterEachAsync()
        {
            Log.Add($"AfterEach:{Context!.SpecId}");
            return Task.CompletedTask;
        }

        [Scenario] public void first() => Given("a step", () => Log.Add("step:first"));
        [Scenario] public void second() => Given("a step", () => Log.Add("step:second"));
    }

    public class Vocabulary : Fixture
    {
        public string SpecId() => Context!.SpecId;
    }

    public class HostingSpecs : Specification
    {
        private readonly Vocabulary _vocabulary;

        public HostingSpecs()
        {
            _vocabulary = Host<Vocabulary>();
        }

        [Scenario]
        public void the_hosted_fixture_is_bound()
        {
            Then("the hosted fixture's spec id", () => _vocabulary.SpecId()).ShouldBe("the hosted fixture is bound");
        }
    }
}
