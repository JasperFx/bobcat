using Bobcat;
using Bobcat.Engine;

namespace Bobcat.Acceptance.Tests;

[IncludeGrammars(typeof(ClockGrammars))]
public class ClockFixture : Fixture
{
    private static DateTime Now => BobcatClock.Current.GetUtcNow().UtcDateTime;

    [Then("the clock date should be {string}")]
    public DateOnly ClockDate() => DateOnly.FromDateTime(Now);

    [Then("the reminder time should be {string}")]
    public DateTime ReminderTime() => Now.AddMinutes(30);

    [Then("the computed due date should be {string}")]
    public DateOnly DueDate() => DateOnly.FromDateTime(Now).AddDays(3);
}
