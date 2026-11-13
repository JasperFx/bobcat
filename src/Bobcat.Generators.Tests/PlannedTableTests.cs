using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#827: a step whose <c>StepTable</c> argument is a compile-time constant carries that table on its
/// planned step, so <c>bobcat preview</c> and the runner page can show the rows — which, for a table
/// step, are the specification. A table built at run time, or passed through a variable, carries
/// nothing: the preview then shows the sentence only, by design.
/// </summary>
public class PlannedTableTests
{
    private const string Source =
        """"
        using System.Threading.Tasks;
        using Bobcat;
        using Xunit;

        namespace Specs;

        public record Order(string Sku, int Quantity);

        public class Grammar
        {
            [Then("the underwriting decisions are")]
            internal void TheDecisionsAre(StepTable table) { }

            [Given("the orders are")]
            internal void TheOrdersAre(StepTable table) { }

            [Then("the total is {total}")]
            internal void TheTotalIs(int total) { }
        }

        [BobcatFeature("Tables 827")]
        public class table_specs
        {
            private readonly Grammar _grammar = new();

            [Fact]
            public void a_raw_string_literal()
            {
                _grammar.TheDecisionsAre("""
                    | Credit Score | Decision |
                    | 579          | Decline  |
                    | 580          | Approve  |
                    """);
            }

            [Fact]
            public void a_constant_interpolation()
            {
                _grammar.TheOrdersAre($"| {nameof(Order.Sku)} | {nameof(Order.Quantity)} |\n| A-1 | 2 |");
            }

            [Fact]
            public void a_table_passed_through_a_variable()
            {
                var rows = "| Credit Score | Decision |\n| 579 | Decline |";
                _grammar.TheDecisionsAre(rows);
            }

            [Fact]
            public void a_table_built_at_run_time()
            {
                _grammar.TheDecisionsAre(new StepTable(new[] { "Credit Score" }, new[] { new[] { "579" } }));
            }

            [Fact]
            public void a_step_with_no_table()
            {
                _grammar.TheTotalIs(3);
            }
        }
        """";

    private static readonly GeneratorHarness.RunOutcome _outcome = GeneratorHarness.Run(Source);

    private static string planFor(string scenario)
    {
        var plan = _outcome.GeneratedSource("PlannedSteps");
        return plan.Split('\n').Single(l => l.Contains($"Register(\"Tables 827/{scenario}\""));
    }

    [Fact]
    public void the_generated_plan_compiles()
    {
        // A multi-line raw string has to arrive as an escaped literal, never as raw line breaks
        _outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void a_raw_string_literal_rides_on_the_planned_step()
    {
        var line = planFor("a raw string literal");

        line.ShouldContain(@"| Credit Score | Decision |\n");
        line.ShouldContain("| 580          | Approve  |");
    }

    [Fact]
    public void a_constant_interpolation_is_read_by_value()
    {
        // nameof(Order.Sku) is a constant, so the header the run will see is the header the preview shows
        planFor("a constant interpolation").ShouldContain(@"| Sku | Quantity |\n| A-1 | 2 |");
    }

    [Fact]
    public void a_table_passed_through_a_variable_carries_no_table()
    {
        var line = planFor("a table passed through a variable");

        line.ShouldContain("\"Grammar.TheDecisionsAre\", null, ");
        line.ShouldNotContain("Credit Score");
    }

    [Fact]
    public void a_table_built_at_run_time_carries_no_table()
    {
        planFor("a table built at run time").ShouldNotContain("Credit Score");
    }

    [Fact]
    public void a_step_without_a_table_parameter_is_unchanged()
    {
        // Five positional arguments, as before #827: the table is a trailing optional the plan leaves off
        planFor("a step with no table").ShouldMatch(@"TheTotalIs"", null, \d+\)\);");
    }
}
