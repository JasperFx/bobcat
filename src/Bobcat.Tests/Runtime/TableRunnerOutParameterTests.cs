using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// <c>TableRunner.Run</c> with <c>out</c> parameters as a decision table's expected outputs
/// (bobcat#483), the way Storyteller built a decision table: ordinary parameters bind from the input
/// columns, each out parameter is an output column judged after the call.
/// </summary>
public class TableRunnerOutParameterTests
{
    public class Arithmetic
    {
        public int Calls { get; private set; }

        public void DivMod(int dividend, int divisor, out int quotient, out int remainder)
        {
            Calls++;
            quotient = dividend / divisor;
            remainder = dividend % divisor;
        }

        // A returned value and an out parameter on one method: a column per output.
        public decimal Total(int quantity, decimal price, out decimal tax)
        {
            var total = quantity * price;
            tax = total * 0.1m;
            return total;
        }

        public void Titled(int amount, [Header("Twice the Amount")] out int doubled) => doubled = amount * 2;

        public void Describe(string name, out string? greeting)
            => greeting = name == "nobody" ? null : $"Hello, {name}";
    }

    private readonly Arithmetic _target = new();

    private static CellResult cell(TableRun run, int row, string column)
        => run.Cells.Single(c => c.RowIndex == row && c.Name == column);

    [Fact]
    public async Task out_parameters_are_outputs_and_the_rest_bind_from_their_columns()
    {
        var run = await TableRunner.Run(_target, nameof(Arithmetic.DivMod), """
            | dividend | divisor | quotient | remainder |
            | 17       | 5       | 3        | 2         |
            | 10       | 3       | 3        | 1         |
            """);

        run.Succeeded.ShouldBeTrue();
        _target.Calls.ShouldBe(2);

        // Inputs are plain, outputs are judged.
        cell(run, 0, "dividend").Status.ShouldBe(ResultStatus.ok);
        cell(run, 0, "divisor").Status.ShouldBe(ResultStatus.ok);
        cell(run, 0, "quotient").Status.ShouldBe(ResultStatus.success);
        cell(run, 1, "remainder").Status.ShouldBe(ResultStatus.success);

        // One cell per column per row, nothing doubled up.
        run.Cells.Count.ShouldBe(8);
    }

    [Fact]
    public async Task a_wrong_out_value_is_a_failed_cell_with_expected_and_actual()
    {
        var run = await TableRunner.Run(_target, nameof(Arithmetic.DivMod), """
            | dividend | divisor | quotient | remainder |
            | 17       | 5       | 3        | 9         |
            """);

        run.Succeeded.ShouldBeFalse();

        var bad = cell(run, 0, "remainder");
        bad.Status.ShouldBe(ResultStatus.failed);
        bad.Expected.ShouldBe("9");
        bad.Actual.ShouldBe("2");

        // The other output on the same row is judged on its own merits.
        cell(run, 0, "quotient").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task a_return_value_and_out_parameters_mix_with_a_column_per_output()
    {
        var run = await TableRunner.Run(_target, nameof(Arithmetic.Total), """
            | quantity | price | tax  | Total |
            | 2        | 10.00 | 2.00 | 20.00 |
            | 4        | 2.00  | 0.50 | 8.00  |
            """);

        // The one column no parameter names, in or out, is the returned value.
        cell(run, 0, "Total").Status.ShouldBe(ResultStatus.success);
        cell(run, 0, "tax").Status.ShouldBe(ResultStatus.success);
        cell(run, 1, "Total").Status.ShouldBe(ResultStatus.success);

        var tax = cell(run, 1, "tax");
        tax.Status.ShouldBe(ResultStatus.failed);
        tax.Expected.ShouldBe("0.50");
        tax.Actual.ShouldBe("0.800");
    }

    [Fact]
    public async Task an_out_parameter_without_a_column_is_not_checked()
    {
        var run = await TableRunner.Run(_target, nameof(Arithmetic.DivMod), """
            | dividend | divisor | quotient |
            | 17       | 5       | 3        |
            """);

        run.Succeeded.ShouldBeTrue();
        run.Cells.ShouldNotContain(c => c.Name == "remainder");
        cell(run, 0, "quotient").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task an_out_parameter_column_never_counts_as_the_returned_value()
    {
        // Without the Total column, the only column no INPUT names is "tax" -- and tax is an out
        // parameter, so it is its own output and the returned value goes unchecked.
        var run = await TableRunner.Run(_target, nameof(Arithmetic.Total), """
            | quantity | price | tax  |
            | 2        | 10.00 | 2.00 |
            """);

        run.Succeeded.ShouldBeTrue();
        run.Cells.Count(c => c.Status == ResultStatus.success).ShouldBe(1);
        cell(run, 0, "tax").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task a_throwing_row_is_a_row_error_and_its_outputs_are_not_invented()
    {
        var run = await TableRunner.Run(_target, nameof(Arithmetic.DivMod), """
            | dividend | divisor | quotient | remainder |
            | 17       | 0       | 3        | 2         |
            | 10       | 3       | 3        | 1         |
            """);

        var error = cell(run, 0, DecisionTableComparer.RowErrorCell);
        error.Status.ShouldBe(ResultStatus.error);
        error.DisplayText.ShouldStartWith("DivideByZeroException");

        run.Cells.Where(c => c.RowIndex == 0).Select(c => c.Name)
            .ShouldBe([DecisionTableComparer.RowErrorCell, "dividend", "divisor"], ignoreOrder: true);

        // The next row still ran.
        cell(run, 1, "quotient").Status.ShouldBe(ResultStatus.success);
        cell(run, 1, "remainder").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task output_columns_match_by_name_case_insensitively_like_inputs()
    {
        var run = await TableRunner.Run(_target, nameof(Arithmetic.DivMod), """
            | Dividend | DIVISOR | Quotient | REMAINDER |
            | 17       | 5       | 3        | 2         |
            """);

        run.Succeeded.ShouldBeTrue();
        cell(run, 0, "Quotient").Status.ShouldBe(ResultStatus.success);
        cell(run, 0, "REMAINDER").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task an_out_parameter_is_known_by_its_header_title()
    {
        var run = await TableRunner.Run(_target, nameof(Arithmetic.Titled), """
            | amount | Twice the Amount |
            | 21     | 42               |
            | 5      | 11               |
            """);

        cell(run, 0, "Twice the Amount").Status.ShouldBe(ResultStatus.success);
        cell(run, 1, "Twice the Amount").Status.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public async Task out_values_go_through_the_same_null_and_blank_rules_as_a_returned_value()
    {
        var run = await TableRunner.Run(_target, nameof(Arithmetic.Describe), """
            | name   | greeting     |
            | Ann    | Hello, Ann   |
            | nobody | NULL         |
            | nobody | Hello, nobody |
            """);

        cell(run, 0, "greeting").Status.ShouldBe(ResultStatus.success);
        cell(run, 1, "greeting").Status.ShouldBe(ResultStatus.success);
        cell(run, 2, "greeting").Status.ShouldBe(ResultStatus.failed);
    }
}
