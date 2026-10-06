using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.Verification;

public class CellResultTests
{
    [Fact]
    public void legacy_constructor_returns_display_text_verbatim()
    {
        var cell = new CellResult("Qty", ResultStatus.failed, "expected '1', got '2'");
        cell.DisplayText.ShouldBe("expected '1', got '2'");
        cell.Expected.ShouldBeNull();
        cell.Actual.ShouldBeNull();
    }

    [Fact]
    public void derives_display_text_for_success()
    {
        var cell = new CellResult("Qty", ResultStatus.success) { Expected = "90", Actual = "90" };
        cell.DisplayText.ShouldBe("90");
    }

    [Fact]
    public void derives_display_text_for_failure()
    {
        var cell = new CellResult("Qty", ResultStatus.failed) { Expected = "90", Actual = "85" };
        cell.DisplayText.ShouldBe("expected '90', got '85'");
    }

    [Fact]
    public void derives_display_text_with_note()
    {
        var cell = new CellResult("Total", ResultStatus.success)
        {
            Expected = "10.0", Actual = "10.01", Note = "±0.1"
        };
        cell.DisplayText.ShouldBe("10.0 (±0.1)");
    }

    [Fact]
    public void derives_display_text_for_invalid_uses_note()
    {
        var cell = new CellResult("Qty", ResultStatus.invalid)
        {
            Expected = "abc", Actual = "5", Note = "'abc' is not a valid int"
        };
        cell.DisplayText.ShouldBe("'abc' is not a valid int");
    }

    // --- What a cell actually compared (issue #384). ---

    [Fact]
    public void an_unstated_comparison_is_equality_so_every_existing_producer_is_unchanged()
    {
        // The compatibility claim, and the reason the field is nullable rather than defaulted in
        // every constructor call: a table, set or property cell says nothing here and means
        // equality, which is what it has always meant.
        var cell = new CellResult("Qty", ResultStatus.failed) { Expected = "90", Actual = "85" };

        cell.Comparison.ShouldBeNull();
        cell.IsEqualityShaped.ShouldBeTrue();
        cell.DisplayText.ShouldBe("expected '90', got '85'");
    }

    [Fact]
    public void an_explicit_equals_reads_exactly_as_an_unstated_one()
    {
        var cell = new CellResult("Qty", ResultStatus.failed)
        {
            Expected = "90", Actual = "85", Comparison = Comparison.Equals
        };

        cell.IsEqualityShaped.ShouldBeTrue();
        cell.DisplayText.ShouldBe("expected '90', got '85'");
    }

    [Fact]
    public void a_non_equality_cell_states_the_comparison_it_made_rather_than_an_equality()
    {
        // THE DEFECT this closed. "expected '10', got '3'" is a claim about equality, and for a
        // greater-than assertion it is false: 10 is the bound. The sentence above the cell read
        // correctly, because the dialect writes the comparison into the step text — so a reader of
        // the console was fine and a consumer rendering cells as a grid saw only the false half.
        var cell = new CellResult("calculator.Value", ResultStatus.failed)
        {
            Expected = "10", Actual = "3", Comparison = Comparison.GreaterThan
        };

        cell.IsEqualityShaped.ShouldBeFalse();
        cell.DisplayText.ShouldBe("should be greater than '10', got '3'");
    }

    [Theory]
    [InlineData(Comparison.NotEquals, "should not be '4', got '4'")]
    [InlineData(Comparison.GreaterThanOrEqual, "should be at least '4', got '4'")]
    [InlineData(Comparison.LessThan, "should be less than '4', got '4'")]
    [InlineData(Comparison.LessThanOrEqual, "should be at most '4', got '4'")]
    [InlineData(Comparison.Contains, "should contain '4', got '4'")]
    [InlineData(Comparison.StartsWith, "should start with '4', got '4'")]
    [InlineData(Comparison.EndsWith, "should end with '4', got '4'")]
    public void every_binary_comparison_says_what_it_checked(Comparison comparison, string expected)
    {
        var cell = new CellResult("x", ResultStatus.failed)
        {
            Expected = "4", Actual = "4", Comparison = comparison
        };

        cell.DisplayText.ShouldBe(expected);
    }

    [Theory]
    [InlineData(Comparison.IsNull, "should be null, got '3'")]
    [InlineData(Comparison.IsNotNull, "should not be null, got '3'")]
    [InlineData(Comparison.IsEmpty, "should be empty, got '3'")]
    [InlineData(Comparison.IsNotEmpty, "should not be empty, got '3'")]
    public void a_unary_comparison_shows_no_expected_value_because_it_had_none(
        Comparison comparison, string expected)
    {
        // ShouldNotBeNull() has no expectation to put beside the actual, so a cell that printed
        // "expected ''" would be inventing one.
        var cell = new CellResult("x", ResultStatus.failed)
        {
            Actual = "3", Comparison = comparison
        };

        comparison.HasExpectedValue().ShouldBeFalse();
        cell.DisplayText.ShouldBe(expected);
    }

    [Fact]
    public void approximately_cannot_read_as_exact_and_keeps_its_tolerance()
    {
        // The issue's own example: rendering ShouldBe(x, 0.01) as though it were exact is a lie
        // the enum makes impossible to tell by accident. The tolerance already travelled in the
        // note, where the built-in checkers put it; what was missing was the word in front.
        var cell = new CellResult("Total", ResultStatus.failed)
        {
            Expected = "9.00", Actual = "9.05", Note = "±0.01", Comparison = Comparison.Approximately
        };

        cell.DisplayText.ShouldBe("should be approximately '9.00', got '9.05' (±0.01)");
    }

    [Fact]
    public void appending_a_note_keeps_the_comparison()
    {
        // Without this, WithNote turned a greater-than cell back into an equality claim — the
        // exact falsehood the comparison exists to prevent, reintroduced by a copy constructor.
        var cell = new CellResult("x", ResultStatus.failed)
            {
                Expected = "10", Actual = "3", Comparison = Comparison.GreaterThan
            }
            .WithNote("retried");

        cell.Comparison.ShouldBe(Comparison.GreaterThan);
        cell.DisplayText.ShouldBe("should be greater than '10', got '3' (retried)");
    }

    [Fact]
    public void a_passing_cell_says_nothing_about_the_comparison_because_there_is_no_disagreement()
    {
        // A green cell shows the value, not the claim. The comparison matters when a cell has to
        // explain itself, and a cell that agreed has nothing to explain.
        var cell = new CellResult("x", ResultStatus.success)
        {
            Expected = "10", Actual = "11", Comparison = Comparison.GreaterThan
        };

        cell.DisplayText.ShouldBe("10");
    }
}
