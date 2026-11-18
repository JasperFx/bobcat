using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#554, the BOBCAT030 replacement: a constant table passed to a step that hands it on to
/// <c>TableRunner.Run</c> or <c>BuildRows&lt;T&gt;</c> is checked at the step's call site against the row
/// method or <c>T</c>. A column binding to nothing is BOBCAT032; a missing column, a short row or a cell the
/// runtime cannot read is BOBCAT042.
/// </summary>
public class ForwardedTableTests
{
    private const string Grammar =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;
        using Bobcat.Runtime;

        namespace Specs;

        public enum Tier { Basic, Gold }

        public record Order(string Customer, int Quantity, DateTime Placed);

        public class Grammar
        {
            [When("the line totals are calculated")]
            public Task TheLineTotalsAre(StepTable table) => TableRunner.Run(this, nameof(lineTotal), table);

            public Task TheDivisionsAre(StepTable table) => TableRunner.Run(this, nameof(divMod), table);

            public Task TheTiersAre(StepTable table) => TableRunner.Run(this, nameof(tiered), table);

            public Task TheEventsAre(StepTable table) => TableRunner.Run(this, nameof(recorded), table);

            public void TheOrdersAre(StepTable table) => TableRunner.BuildRows<Order>(table);

            public Task ThroughAHelper(StepTable table) => helper(table);
            private Task helper(StepTable rows) => TableRunner.Run(this, nameof(lineTotal), rows);

            public Task FourDeep(StepTable table) => one(table);
            private Task one(StepTable t) => two(t);
            private Task two(StepTable t) => three(t);
            private Task three(StepTable t) => TableRunner.Run(this, nameof(lineTotal), t);

            public async Task Rebuilt(StepTable table) => await TableRunner.Run(this, nameof(lineTotal), StepTable.Parse("| price | quantity |"));

            private decimal lineTotal(decimal price, int quantity, [Header("off %")] int discount = 0) => price * quantity;
            private void divMod(int a, int b, out int quotient, out int remainder) { quotient = a / b; remainder = a % b; }
            private void tiered(Tier tier, int? limit) { }
            private int recorded(DateTime at, Guid id) => 1;
        }
        """;

    private const string LineTotals = "| price | quantity | total |\n| 2.5 | 4 | 10 |";

    private static GeneratorHarness.RunOutcome run(string body)
        => GeneratorHarness.Run(Grammar + "\n" + $$"""
            public class Specs
            {
                private const string Constant = "| price | quantity | total |\n| 2.5 | lots | 10 |";
                private readonly Grammar _grammar = new();

                public async Task Check(string notConstant)
                {
            {{body}}
                }
            }
            """);

    private static IReadOnlyList<string> errors(GeneratorHarness.RunOutcome outcome, string id)
        => outcome.WithId(id).Select(d => d.GetMessage()).ToList();

    private static void clean(GeneratorHarness.RunOutcome outcome)
    {
        errors(outcome, "BOBCAT032").ShouldBeEmpty();
        errors(outcome, "BOBCAT042").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void a_decision_table_that_fits_its_row_method_is_clean()
    {
        clean(run($"""
                   await _grammar.TheLineTotalsAre("{LineTotals.Replace("\n", "\\n")}");
                   await _grammar.TheLineTotalsAre("| price | quantity | off % | total |\n| 2.5 | 4 | 10 | 9 |");
                   await _grammar.TheDivisionsAre("| a | b | quotient | remainder |\n| 7 | 2 | 3 | 1 |\n| 9 | 3 | | |");
                   await _grammar.TheTiersAre("| tier | limit |\n| gold | NULL |\n| Basic | 5 |");
                   """));
    }

    [Fact]
    public void a_cell_its_parameter_cannot_hold_is_BOBCAT042_naming_the_step_the_row_method_and_the_cell()
    {
        var message = errors(run("""
                                 await _grammar.TheLineTotalsAre("| price | quantity | total |\n| 2.5 | 4 | 10 |\n| 3 | lots | 9 |");
                                 """), "BOBCAT042").ShouldHaveSingleItem();

        message.ShouldContain("TheLineTotalsAre");
        message.ShouldContain("Grammar.lineTotal");
        message.ShouldContain("row 2");
        message.ShouldContain("'lots'");
        message.ShouldContain("'quantity'");
        message.ShouldContain("int");
    }

    [Fact]
    public void an_enum_cell_naming_no_member_lists_the_members()
    {
        var message = errors(run("""
                                 await _grammar.TheTiersAre("| tier | limit |\n| Platinum | 1 |");
                                 """), "BOBCAT042").ShouldHaveSingleItem();

        message.ShouldContain("'Platinum' is not one of Tier's values (Basic, Gold)");
    }

    [Fact]
    public void NULL_under_a_non_nullable_value_type_is_refused()
    {
        var message = errors(run("""
                                 await _grammar.TheLineTotalsAre("| price | quantity | total |\n| 2 | NULL | 0 |");
                                 """), "BOBCAT042").ShouldHaveSingleItem();

        message.ShouldContain("cannot be null");
    }

    [Fact]
    public void a_renamed_parameter_is_a_stale_column_and_a_missing_one()
    {
        var outcome = run("""
                          await _grammar.TheLineTotalsAre("| price | qty | total |\n| 2 | 4 | 8 |");
                          """);

        // Two columns no parameter claims: neither can be the return value's, so both are stale
        errors(outcome, "BOBCAT032").Count.ShouldBe(2);
        errors(outcome, "BOBCAT032").ShouldContain(m => m.Contains("'qty' names no parameter") && m.Contains("this table has 2: qty, total"));
        errors(outcome, "BOBCAT042").ShouldHaveSingleItem().ShouldContain("no 'quantity' column for the parameter 'quantity'");
    }

    [Fact]
    public void a_column_binding_to_nothing_on_a_row_method_returning_nothing_is_BOBCAT032()
    {
        var message = errors(run("""
                                 await _grammar.TheTiersAre("| tier | limit | note |\n| Gold | 1 | vip |");
                                 """), "BOBCAT032").ShouldHaveSingleItem();

        message.ShouldContain("'note' names no parameter of it (it takes tier, limit)");
        message.ShouldContain("returns nothing to compare the column against");
    }

    [Fact]
    public void a_row_too_short_to_reach_a_required_parameter_is_refused()
    {
        errors(run("""
                   await _grammar.TheLineTotalsAre("| price | quantity | total |\n| 2 |");
                   """), "BOBCAT042").ShouldHaveSingleItem().ShouldContain("gets no value from column 'quantity'");
    }

    [Fact]
    public void a_date_or_a_guid_shaped_value_the_runtime_reads_is_left_alone_but_a_bad_guid_is_not()
    {
        clean(run("""
                  await _grammar.TheEventsAre("| at | id | result |\n| TODAY+1 | 6f9619ff-8b86-d011-b42d-00c04fc964ff | 1 |");
                  """));

        errors(run("""
                   await _grammar.TheEventsAre("| at | id | result |\n| TODAY | not-a-guid | 1 |");
                   """), "BOBCAT042").ShouldHaveSingleItem().ShouldContain("it is not a Guid");
    }

    [Fact]
    public void a_constant_field_is_checked_like_a_literal()
    {
        errors(run("""
                   await _grammar.TheLineTotalsAre(Constant);
                   """), "BOBCAT042").ShouldHaveSingleItem().ShouldContain("'lots'");
    }

    [Fact]
    public void a_table_that_is_not_constant_is_left_to_run_time()
    {
        clean(run("""
                  await _grammar.TheLineTotalsAre(notConstant);
                  """));
    }

    [Fact]
    public void the_table_is_followed_through_a_helper_and_no_further_than_three_deep()
    {
        errors(run("""
                   await _grammar.ThroughAHelper("| price | quantity | total |\n| 2 | lots | 8 |");
                   """), "BOBCAT042").ShouldHaveSingleItem().ShouldContain("ThroughAHelper");

        clean(run("""
                  await _grammar.FourDeep("| price | quantity | total |\n| 2 | lots | 8 |");
                  """));
    }

    [Fact]
    public void a_step_that_does_not_pass_its_own_table_on_is_not_checked_against_it()
    {
        clean(run("""
                  await _grammar.Rebuilt("| nonsense |\n| x |");
                  """));
    }

    [Fact]
    public void a_direct_call_to_TableRunner_is_checked_too()
    {
        var outcome = GeneratorHarness.Run(Grammar + "\n" + """
            public class Direct
            {
                public Task Run() => TableRunner.Run(this, nameof(row), "| amount |\n| ten |");
                private void row(decimal amount) { }
            }
            """);

        var message = errors(outcome, "BOBCAT042").ShouldHaveSingleItem();
        message.ShouldStartWith("The table runs its rows through Direct.row");
        message.ShouldContain("'ten'");
    }

    [Fact]
    public void BuildRows_checks_columns_against_the_type_and_cells_against_its_members()
    {
        clean(run("""
                  _grammar.TheOrdersAre("| Customer | Quantity | Placed |\n| Ann | 2 | TODAY |\n| Bob | | |");
                  """));

        var outcome = run("""
                          _grammar.TheOrdersAre("| Customr | Quantity |\n| Ann | two |");
                          """);

        errors(outcome, "BOBCAT032").ShouldHaveSingleItem().ShouldContain("'Customr' does not match Order");
        errors(outcome, "BOBCAT042").ShouldHaveSingleItem().ShouldContain("'two' in column 'Quantity' cannot be read as int");
    }
}
