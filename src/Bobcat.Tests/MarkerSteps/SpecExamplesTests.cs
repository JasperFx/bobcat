using System.Reflection;
using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// bobcat#570: a <c>[BobcatExamples]</c> table read into one argument list per row, by the rules
/// <c>TableRunner</c> binds a row method by and <c>CellValues</c> reads a cell by.
/// </summary>
public class SpecExamplesTests
{
    public enum Tier { None, Silver, Gold }

    private static MethodInfo method(string name)
        => typeof(SpecExamplesTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    // ReSharper disable UnusedParameter.Local
    private static void discount(decimal amount, Tier tier, decimal expected) { }
    private static void titled([Header("Customer name")] string name, int? count, Tier tier = Tier.Gold) { }
    private static void dated(DateTime when) { }
    // ReSharper restore UnusedParameter.Local

    [Fact]
    public void each_row_is_one_argument_list_in_parameter_order()
    {
        var rows = SpecExamples.Rows(method(nameof(discount)),
            """
            | tier | amount | expected |
            | gold | 100    | 90       |
            | None | 100.50 | 100.50   |
            """);

        rows.Count.ShouldBe(2);
        rows[0].ShouldBe([100m, Tier.Gold, 90m]);
        rows[1].ShouldBe([100.50m, Tier.None, 100.50m]);
    }

    [Fact]
    public void a_header_title_names_the_column_and_a_default_fills_a_missing_one()
    {
        var rows = SpecExamples.Rows(method(nameof(titled)),
            """
            | customer NAME | count |
            | EMPTY         | NULL  |
            """);

        rows.ShouldHaveSingleItem().ShouldBe(["", null, Tier.Gold]);
    }

    [Fact]
    public void a_relative_time_reads_against_the_bobcat_clock()
        => SpecExamples.Rows(method(nameof(dated)), "| when |\n| TODAY+2 |")
            .ShouldHaveSingleItem()[0].ShouldBe(BobcatClock.Current.GetUtcNow().UtcDateTime.Date.AddDays(2));

    [Fact]
    public void a_column_naming_no_parameter_is_refused_by_name()
        => Should.Throw<ArgumentException>(() => SpecExamples.Rows(method(nameof(discount)), "| amount | level | tier | expected |\n| 1 | 2 | Gold | 3 |"))
            .Message.ShouldBe("[BobcatExamples] on SpecExamplesTests.discount: the column 'level' names no parameter (it takes amount, tier, expected).");

    [Fact]
    public void a_parameter_with_no_column_and_no_default_is_refused()
        => Should.Throw<ArgumentException>(() => SpecExamples.Rows(method(nameof(discount)), "| amount | tier |\n| 1 | Gold |"))
            .Message.ShouldContain("no column for the parameter 'expected'");

    [Fact]
    public void a_ragged_row_is_refused()
        => Should.Throw<ArgumentException>(() => SpecExamples.Rows(method(nameof(discount)), "| amount | tier | expected |\n| 1 | Gold |"))
            .Message.ShouldContain("row 1 has 2 cell(s) where the header has 3");

    [Fact]
    public void an_unreadable_cell_names_its_row_and_column()
        => Should.Throw<BadCellException>(() => SpecExamples.Rows(method(nameof(discount)), "| amount | tier | expected |\n| 1 | Platinum | 2 |"))
            .Message.ShouldStartWith("[BobcatExamples] on SpecExamplesTests.discount, row 1, column 'tier': The cell 'Platinum' could not be read as Tier");
}
