using Bobcat.Engine.Verification;
using Shouldly;

namespace Bobcat.Tests.Verification;

public class FriendlyTimeSpanParserTests
{
    [Theory]
    [InlineData("00:05:00", 0, 0, 5, 0)]
    [InlineData("1.02:03:04", 1, 2, 3, 4)]
    [InlineData("5 minutes", 0, 0, 5, 0)]
    [InlineData("2 hours", 0, 2, 0, 0)]
    [InlineData("3 days", 3, 0, 0, 0)]
    [InlineData("30s", 0, 0, 0, 30)]
    [InlineData("30 sec", 0, 0, 0, 30)]
    [InlineData("1d2h30m", 1, 2, 30, 0)]
    [InlineData("1 day 2 hours 30 minutes", 1, 2, 30, 0)]
    [InlineData("1d 2h 30m 15s", 1, 2, 30, 15)]
    public void parses_valid_durations(string text, int days, int hours, int minutes, int seconds)
    {
        FriendlyTimeSpanParser.TryParse(text, out var result).ShouldBeTrue();
        result.ShouldBe(new TimeSpan(days, hours, minutes, seconds));
    }

    [Fact]
    public void parses_fractional_seconds()
    {
        FriendlyTimeSpanParser.TryParse("1.5 seconds", out var result).ShouldBeTrue();
        result.ShouldBe(TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void parses_milliseconds_without_colliding_with_minutes()
    {
        FriendlyTimeSpanParser.TryParse("250ms", out var result).ShouldBeTrue();
        result.ShouldBe(TimeSpan.FromMilliseconds(250));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a duration")]
    [InlineData("5 bananas")]
    [InlineData("abc:def")]
    public void rejects_invalid_durations(string text)
    {
        FriendlyTimeSpanParser.TryParse(text, out _).ShouldBeFalse();
    }
}
