using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Shouldly;

namespace Bobcat.Tests.Verification;

public class CellCheckTests
{
    [Fact]
    public void for_produces_structured_cell_result_on_success()
    {
        var cell = CellCheck.For("Qty", 90, "90");
        cell.Status.ShouldBe(ResultStatus.success);
        cell.Expected.ShouldBe("90");
        cell.Actual.ShouldBe("90");
    }

    [Fact]
    public void for_produces_structured_cell_result_on_failure_with_row_index()
    {
        var cell = CellCheck.For("Qty", 90, "85", null, rowIndex: 3);
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("85");
        cell.Actual.ShouldBe("90");
        cell.RowIndex.ShouldBe(3);
    }

    [Fact]
    public void for_maps_invalid_to_invalid_status()
    {
        var cell = CellCheck.For("Qty", 90, "abc");
        cell.Status.ShouldBe(ResultStatus.invalid);
        cell.Note.ShouldNotBeNull();
    }

    // --- NULL / EMPTY tokens ---

    [Fact]
    public void null_token_matches_null_actual()
    {
        CellCheck.Check<string?>(null, "NULL").Outcome.ShouldBe(CheckOutcome.Match);
    }

    [Fact]
    public void null_token_mismatches_non_null_actual()
    {
        CellCheck.Check(5, "NULL").Outcome.ShouldBe(CheckOutcome.Mismatch);
        CellCheck.Check("NULL", "NULL").Outcome.ShouldBe(CheckOutcome.Mismatch);
    }

    [Fact]
    public void quoted_null_is_a_literal_not_a_token()
    {
        CellCheck.Check("NULL", "\"NULL\"").Outcome.ShouldBe(CheckOutcome.Match);
    }

    [Fact]
    public void empty_token_matches_empty_values()
    {
        CellCheck.Check("", "EMPTY").Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check<string?>(null, "EMPTY").Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check(Array.Empty<int>(), "EMPTY").Outcome.ShouldBe(CheckOutcome.Match);
    }

    [Fact]
    public void empty_token_mismatches_non_empty_values()
    {
        CellCheck.Check("x", "EMPTY").Outcome.ShouldBe(CheckOutcome.Mismatch);
        CellCheck.Check(new[] { 1 }, "EMPTY").Outcome.ShouldBe(CheckOutcome.Mismatch);
    }

    // --- runtime dispatch (ForValue) ---

    [Fact]
    public void for_value_dispatches_on_runtime_type()
    {
        object boxed = 90;
        var cell = CellCheck.ForValue("Qty", boxed, "90");
        cell.Status.ShouldBe(ResultStatus.success);
        cell.Expected.ShouldBe("90");
    }

    [Fact]
    public void for_value_handles_null_actual_with_token()
    {
        CellCheck.ForValue("Name", null, "NULL").Status.ShouldBe(ResultStatus.success);
        CellCheck.ForValue("Name", null, "EMPTY").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void for_value_null_actual_mismatches_concrete_expected()
    {
        var cell = CellCheck.ForValue("Name", null, "Widget");
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Actual.ShouldBe("NULL");
        cell.Expected.ShouldBe("Widget");
    }

    // --- resolution chain ---

    [Fact]
    public void comparison_type_override_takes_precedence()
    {
        var opts = new CheckOptions { ComparisonType = typeof(AlwaysMatchIntChecker) };
        CellCheck.Check(5, "999", opts).Outcome.ShouldBe(CheckOutcome.Match);
    }

    [Fact]
    public void registered_checker_beats_default_resolution()
    {
        // Widget has no built-in checker; default resolution would fail to convert.
        CellCheck.Check(new Widget(1), "anything").Outcome.ShouldBe(CheckOutcome.Invalid);

        try
        {
            CellCheck.Register(new WidgetChecker());
            CellCheck.Check(new Widget(1), "1").Outcome.ShouldBe(CheckOutcome.Match);
            CellCheck.Check(new Widget(1), "2").Outcome.ShouldBe(CheckOutcome.Mismatch);
        }
        finally
        {
            CellCheck.Unregister<Widget>();
        }

        CellCheck.Check(new Widget(1), "1").Outcome.ShouldBe(CheckOutcome.Invalid);
    }

    private readonly struct Widget
    {
        public Widget(int id) => Id = id;
        public int Id { get; }
    }

    private sealed class WidgetChecker : IValueChecker<Widget>
    {
        public CheckResult Check(Widget actual, string expectedText, CheckOptions options)
        {
            if (!int.TryParse(expectedText.Trim(), out var id))
                return CheckResult.Invalid(expectedText, actual.Id.ToString());
            return id == actual.Id
                ? CheckResult.Match(id.ToString(), actual.Id.ToString())
                : CheckResult.Mismatch(id.ToString(), actual.Id.ToString());
        }
    }

    public sealed class AlwaysMatchIntChecker : IValueChecker<int>
    {
        public CheckResult Check(int actual, string expectedText, CheckOptions options)
            => CheckResult.Match(expectedText, actual.ToString(), "always");
    }
}
