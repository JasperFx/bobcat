using Bobcat.Engine.Verification;
using Shouldly;

namespace Bobcat.Tests.Verification;

public class BuiltInCheckerTests
{
    [Theory]
    [InlineData(90, "90", CheckOutcome.Match)]
    [InlineData(90, "85", CheckOutcome.Mismatch)]
    [InlineData(90, " 90 ", CheckOutcome.Match)]
    [InlineData(90, "abc", CheckOutcome.Invalid)]
    public void int_checker(int actual, string expected, CheckOutcome outcome)
    {
        CellCheck.Check(actual, expected).Outcome.ShouldBe(outcome);
    }

    [Fact]
    public void int_checker_with_tolerance()
    {
        var opts = new CheckOptions { Tolerance = 2 };
        CellCheck.Check(101, "100", opts).Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check(103, "100", opts).Outcome.ShouldBe(CheckOutcome.Mismatch);
    }

    [Fact]
    public void double_checker_with_tolerance_records_note()
    {
        var opts = new CheckOptions { Tolerance = 0.01 };
        var result = CellCheck.Check(10.005, "10.0", opts);
        result.Outcome.ShouldBe(CheckOutcome.Match);
        result.Note.ShouldBe("±0.01");
    }

    [Fact]
    public void decimal_checker()
    {
        CellCheck.Check(19.99m, "19.99", null).Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check(19.99m, "20.00", null).Outcome.ShouldBe(CheckOutcome.Mismatch);
    }

    [Theory]
    [InlineData(true, "true", CheckOutcome.Match)]
    [InlineData(true, "yes", CheckOutcome.Match)]
    [InlineData(false, "0", CheckOutcome.Match)]
    [InlineData(true, "false", CheckOutcome.Mismatch)]
    [InlineData(true, "maybe", CheckOutcome.Invalid)]
    public void bool_checker(bool actual, string expected, CheckOutcome outcome)
    {
        CellCheck.Check(actual, expected).Outcome.ShouldBe(outcome);
    }

    [Fact]
    public void datetime_checker()
    {
        var actual = new DateTime(2026, 6, 5, 10, 30, 0);
        CellCheck.Check(actual, "2026-06-05 10:30:00").Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check(actual, "2026-06-06 10:30:00").Outcome.ShouldBe(CheckOutcome.Mismatch);
        CellCheck.Check(actual, "not-a-date").Outcome.ShouldBe(CheckOutcome.Invalid);
    }

    [Fact]
    public void dateonly_checker()
    {
        CellCheck.Check(new DateOnly(2026, 6, 5), "2026-06-05").Outcome.ShouldBe(CheckOutcome.Match);
    }

    [Fact]
    public void timeonly_checker()
    {
        CellCheck.Check(new TimeOnly(10, 30), "10:30").Outcome.ShouldBe(CheckOutcome.Match);
    }

    [Fact]
    public void timespan_checker_uses_friendly_parser()
    {
        CellCheck.Check(TimeSpan.FromMinutes(5), "5 minutes").Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check(TimeSpan.FromMinutes(5), "00:05:00").Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check(TimeSpan.FromMinutes(5), "6 minutes").Outcome.ShouldBe(CheckOutcome.Mismatch);
    }

    [Fact]
    public void guid_checker()
    {
        var g = Guid.NewGuid();
        CellCheck.Check(g, g.ToString()).Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check(g, Guid.NewGuid().ToString()).Outcome.ShouldBe(CheckOutcome.Mismatch);
        CellCheck.Check(g, "not-a-guid").Outcome.ShouldBe(CheckOutcome.Invalid);
    }

    public enum Color { Red, Green, Blue }

    [Theory]
    [InlineData(Color.Green, "Green", CheckOutcome.Match)]
    [InlineData(Color.Green, "green", CheckOutcome.Match)] // case-insensitive by default
    [InlineData(Color.Green, "1", CheckOutcome.Match)]     // numeric
    [InlineData(Color.Green, "Blue", CheckOutcome.Mismatch)]
    [InlineData(Color.Green, "Purple", CheckOutcome.Invalid)]
    public void enum_checker(Color actual, string expected, CheckOutcome outcome)
    {
        CellCheck.Check(actual, expected).Outcome.ShouldBe(outcome);
    }

    [Theory]
    [InlineData("hello", "hello", CheckOutcome.Match)]
    [InlineData("hello", "  hello  ", CheckOutcome.Match)] // trimmed by default
    [InlineData("hello", "world", CheckOutcome.Mismatch)]
    public void string_checker_trims_by_default(string actual, string expected, CheckOutcome outcome)
    {
        CellCheck.Check(actual, expected).Outcome.ShouldBe(outcome);
    }

    [Fact]
    public void string_checker_is_case_sensitive_by_default()
    {
        CellCheck.Check("Hello", "hello").Outcome.ShouldBe(CheckOutcome.Mismatch);
        CellCheck.Check("Hello", "hello", new CheckOptions { CaseSensitive = false })
            .Outcome.ShouldBe(CheckOutcome.Match);
    }

    [Fact]
    public void string_checker_preserves_whitespace_when_quoted()
    {
        // Quoted expected opts out of trimming on both sides.
        CellCheck.Check(" value ", "\" value \"").Outcome.ShouldBe(CheckOutcome.Match);
        CellCheck.Check("value", "\" value \"").Outcome.ShouldBe(CheckOutcome.Mismatch);
    }
}
