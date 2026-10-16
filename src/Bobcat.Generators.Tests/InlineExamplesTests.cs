using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#570: a <c>[BobcatExamples]</c> table is read at compile time, so each row is listed under the
/// identity it will run as, a column naming no parameter is BOBCAT032, and a table the runtime would
/// refuse is BOBCAT042.
/// </summary>
public class InlineExamplesTests
{
    private const string Runners =
        """
        using System;

        namespace Xunit
        {
            public class TheoryAttribute : Attribute { }
        }

        namespace Bobcat.Xunit
        {
            [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
            public sealed class BobcatExamplesAttribute : Attribute
            {
                public BobcatExamplesAttribute(string table) { }
            }

            public class BobcatTheoryAttribute : global::Xunit.TheoryAttribute
            {
                public BobcatTheoryAttribute() { }
                public BobcatTheoryAttribute(Type sliceType) { }
            }
        }

        """;

    private static GeneratorHarness.RunOutcome run(string specs)
        => GeneratorHarness.Run(Runners +
                                """
                                namespace Specs
                                {
                                    using Bobcat;
                                    using Bobcat.Xunit;

                                    public enum Tier { None, Silver, Gold }

                                """ + specs + "\n}");

    private static string registration(GeneratorHarness.RunOutcome outcome) => outcome.GeneratedSource("DeclaredSteps");

    private static IEnumerable<string> messages(GeneratorHarness.RunOutcome outcome, string id)
        => outcome.WithId(id).Select(d => d.GetMessage());

    [Fact]
    public void each_row_of_the_table_is_bound_under_its_own_identity()
    {
        var outcome = run(
            """
            public record ApplyDiscount(decimal Amount);

            [BobcatFeature("Discounts")]
            public class DiscountSpecs
            {
                [BobcatTheory(typeof(ApplyDiscount))]
                [BobcatExamples(\"\"\"
                    | amount | tier | expected |
                    | 100    | Gold | 90       |
                    | 100.50 | none | 100.50   |
                    \"\"\")]
                public void discount_is_applied(decimal amount, Tier tier, decimal expected) { }
            }
            """.Replace("\\\"", "\""));

        outcome.Diagnostics.Where(d => d.Id.StartsWith("BOBCAT")).ShouldBeEmpty();

        var source = registration(outcome);
        source.ShouldContain(
            """DeclaredSteps.Bind("Discounts/discount is applied (amount: 100, tier: Gold, expected: 90)", "Specs.DiscountSpecs", "discount_is_applied", "Discounts/discount is applied", "ApplyDiscount", null, null, null);""");

        // An enum is named as the runtime's ToString names it, whatever case the cell used
        source.ShouldContain("\"Discounts/discount is applied (amount: 100.50, tier: None, expected: 100.50)\"");
    }

    [Fact]
    public void the_tokens_a_header_title_and_a_default_read_as_the_runtime_reads_them()
    {
        var outcome = run(
            """
            [BobcatFeature("Tokens")]
            public class TokenSpecs
            {
                [BobcatTheory]
                [BobcatExamples(\"\"\"
                    | Customer name | nickname | count | flag  |
                    | "NULL"        | NULL     | NULL  | TRUE  |
                    | EMPTY         | EMPTY    |       | false |
                    \"\"\")]
                public void tokens([Header("Customer name")] string name, string? nickname, int? count, bool flag, Tier tier = Tier.Gold) { }
            }
            """.Replace("\\\"", "\""));

        outcome.Diagnostics.Where(d => d.Id.StartsWith("BOBCAT")).ShouldBeEmpty();

        var source = registration(outcome);
        source.ShouldContain("\"Tokens/tokens (name: NULL, nickname: null, count: null, flag: true, tier: Gold)\"");
        source.ShouldContain("\"Tokens/tokens (name: , nickname: , count: null, flag: false, tier: Gold)\"");
    }

    [Fact]
    public void a_constant_built_from_nameof_is_read_too()
    {
        var outcome = run(
            """
            [BobcatFeature("Constants")]
            public class ConstantSpecs
            {
                [BobcatTheory]
                [BobcatExamples($"| {nameof(amount)} |\n| 7 |")]
                public void constant(int amount) { }
            }
            """);

        registration(outcome).ShouldContain("\"Constants/constant (amount: 7)\"");
    }

    [Fact]
    public void a_row_whose_value_only_the_run_can_read_is_left_to_run_time()
    {
        var outcome = run(
            """
            [BobcatFeature("Dates")]
            public class DateSpecs
            {
                [BobcatTheory]
                [BobcatExamples(\"\"\"
                    | when    | amount |
                    | TODAY+2 | 1      |
                    \"\"\")]
                public void dated(System.DateTime when, int amount) { }
            }
            """.Replace("\\\"", "\""));

        outcome.Diagnostics.Where(d => d.Id.StartsWith("BOBCAT")).ShouldBeEmpty();

        var source = registration(outcome);
        source.ShouldContain("""DeclaredSteps.Bind("Dates/dated", "Specs.DateSpecs", "dated");""");
        source.ShouldNotContain("Dates/dated (");
    }

    [Fact]
    public void a_column_naming_no_parameter_is_bobcat032()
    {
        var outcome = run(
            """
            [BobcatFeature("Discounts")]
            public class DiscountSpecs
            {
                [BobcatTheory]
                [BobcatExamples(\"\"\"
                    | amount | level |
                    | 100    | Gold  |
                    \"\"\")]
                public void discount_is_applied(int amount, Tier level2 = Tier.None) { }
            }
            """.Replace("\\\"", "\""));

        var error = outcome.WithId("BOBCAT032").ShouldHaveSingleItem();
        error.GetMessage().ShouldBe(
            "The column 'level' of [BobcatExamples] on 'discount_is_applied' names no parameter of it (it takes amount, level2).");

        // At the table, where the stale column is written
        error.Location.SourceTree!.GetText(default).ToString(error.Location.SourceSpan).ShouldStartWith("\"\"\"");

        // A row of a table the runtime would refuse is never listed
        registration(outcome).ShouldNotContain("Discounts/discount is applied (");
    }

    [Fact]
    public void a_table_the_runtime_would_refuse_is_bobcat040()
    {
        var outcome = run(
            """
            [BobcatFeature("Refused")]
            public class RefusedSpecs
            {
                [BobcatTheory]
                [BobcatExamples(\"\"\"
                    | amount | tier     | count |
                    | ten    | Platinum | NULL  |
                    | 1      | Gold     |
                    \"\"\")]
                public void cells(int amount, Tier tier, int count) { }

                [BobcatTheory]
                [BobcatExamples(\"\"\"
                    | amount | Amount |
                    | 1      | 2      |
                    \"\"\")]
                public void twice(int amount, int other) { }

                [BobcatTheory]
                [BobcatExamples("| amount |")]
                public void empty(int amount) { }
            }
            """.Replace("\\\"", "\""));

        var errors = messages(outcome, "BOBCAT042").ToList();
        errors.ShouldContain("Row 1 of [BobcatExamples] on 'cells': the cell 'ten' in column 'amount' cannot be read as int: it is not a Int32.");
        errors.ShouldContain("Row 1 of [BobcatExamples] on 'cells': the cell 'Platinum' in column 'tier' cannot be read as Specs.Tier: 'Platinum' is not one of Tier's values (None, Silver, Gold).");
        errors.ShouldContain("Row 1 of [BobcatExamples] on 'cells': the cell 'NULL' in column 'count' cannot be read as int: Int32 cannot be null.");
        errors.ShouldContain("Row 2 of [BobcatExamples] on 'cells' has 2 cell(s) where the header has 3.");
        errors.ShouldContain("The column 'Amount' of [BobcatExamples] on 'twice' is given twice.");
        errors.ShouldContain("[BobcatExamples] on 'twice' has no column for the parameter 'other', which has no default value. Add a 'other' column.");
        errors.ShouldContain("[BobcatExamples] on 'empty' has a header and no rows, so the theory would have no data. Add one row per example.");
        errors.Count.ShouldBe(7);
    }

    [Fact]
    public void two_rows_alike_are_bobcat036()
    {
        var outcome = run(
            """
            [BobcatFeature("Discounts")]
            public class DiscountSpecs
            {
                [BobcatTheory]
                [BobcatExamples(\"\"\"
                    | amount |
                    | 1      |
                    | 01     |
                    \"\"\")]
                public void discount_is_applied(int amount) { }
            }
            """.Replace("\\\"", "\""));

        outcome.WithId("BOBCAT036").Count().ShouldBe(2);
    }

    /// <summary>
    /// The listed identity and the identity the row runs as are computed in two assemblies; this runs
    /// the same table through both.
    /// </summary>
    [Fact]
    public void the_listed_identity_is_the_one_the_runtime_gives_the_row()
    {
        const string table =
            """
            | amount  | tier   | expected | ratio | note    | id                                   |
            | 100.50  | gold   | 90       | 0.1   | "EMPTY" | 0F8FAD5B-D9CB-469F-A165-70867728950E |
            | -3      | Silver | 1e3      | 2     | EMPTY   | 0f8fad5b-d9cb-469f-a165-70867728950e |
            """;

        var outcome = run(
            "[BobcatFeature(\"Agreement\")] public class AgreementSpecs { [BobcatTheory] [BobcatExamples(\"\"\"\n"
            + table + "\n\"\"\")] public void agree(decimal amount, Tier tier, double expected, float ratio, string note, System.Guid id) { } }");

        outcome.Diagnostics.Where(d => d.Id.StartsWith("BOBCAT")).ShouldBeEmpty();

        List<string?> listed = Regex.Matches(registration(outcome), "\"Agreement/agree (\\([^\"]*\\))\"")
            .Select(m => (string?)m.Groups[1].Value)
            .ToList();

        var method = typeof(InlineExamplesTests).GetMethod(nameof(agree), BindingFlags.NonPublic | BindingFlags.Static)!;
        var names = method.GetParameters().Select(p => p.Name!).ToList();
        var atRunTime = SpecExamples.Rows(method, table).Select(row => SpecRows.Describe(names, row, null)).ToList();

        listed.ShouldBe(atRunTime);
    }

    public enum Tier { None, Silver, Gold }

    // ReSharper disable UnusedParameter.Local
    private static void agree(decimal amount, Tier tier, double expected, float ratio, string note, Guid id) { }
}
