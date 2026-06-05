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
}
