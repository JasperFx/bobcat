using System.Globalization;
using Bobcat.Engine;
using Bobcat.Engine.Verification;

namespace Bobcat;

/// <summary>
/// Shared clock-control grammar, composed into a fixture via
/// <c>[IncludeGrammars(typeof(ClockGrammars))]</c>. Freezes and advances the ambient
/// <see cref="BobcatClock"/>; combine with the relative <c>TODAY</c>/<c>NOW</c> tokens in
/// expected values to assert relative dates deterministically.
/// </summary>
public class ClockGrammars
{
    [Given("the date is {string}")]
    public void SetDate(string date)
    {
        var d = DateOnly.Parse(date, CultureInfo.InvariantCulture);
        BobcatClock.Controllable().SetUtcNow(new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    [Given("the current time is {string}")]
    public void SetTime(string datetime)
    {
        BobcatClock.Controllable().SetUtcNow(ParseUtc(datetime));
    }

    [When("the clock advances by {string}")]
    public void AdvanceBy(string duration)
    {
        BobcatClock.Controllable().Advance(ParseDuration(duration));
    }

    [When("{string} passes")]
    public void Passes(string duration)
    {
        BobcatClock.Controllable().Advance(ParseDuration(duration));
    }

    [When("the clock advances to {string}")]
    public void AdvanceTo(string datetime)
    {
        BobcatClock.Controllable().SetUtcNow(ParseUtc(datetime));
    }

    private static DateTimeOffset ParseUtc(string text)
    {
        var dt = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
    }

    private static TimeSpan ParseDuration(string text)
    {
        if (!FriendlyTimeSpanParser.TryParse(text, out var ts))
            throw new FormatException($"'{text}' is not a valid duration");
        return ts;
    }
}
