using Bobcat.Engine;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// <c>Clock.feature</c>'s last scenario, from the projected lane: an expected value written as a
/// relative token (<c>TODAY+3</c>, <c>NOW + 30 minutes</c>) resolves against the frozen
/// <see cref="BobcatClock"/>, and the cell shows the resolved value with the token as its note.
/// </summary>
/// <remarks>
/// The clock steps themselves (freeze, advance by, passes, advance to) are pinned by
/// <c>Bobcat.CritterStackScenario.Tests</c>'s <c>clock_steps_render_and_move_the_ambient_clock</c>.
/// What is pinned here is the expected-side resolution a step's cell goes through, which is
/// <see cref="SpecAssert"/>'s text-expected form and the same <c>CellCheck</c> the Gherkin lane uses.
/// </remarks>
public class RelativeTimeTests : IDisposable
{
    public class ClockGrammar
    {
        private static DateTime now => BobcatClock.Current.GetUtcNow().UtcDateTime;

        [Then("the clock date should be {date}")]
        internal void ClockDate(string date) => SpecAssert.Check("date", DateOnly.FromDateTime(now), date);

        [Then("the reminder time should be {reminder}")]
        internal void ReminderTime(string reminder) => SpecAssert.Check("reminder", now.AddMinutes(30), reminder);

        [Then("the computed due date should be {due}")]
        internal void DueDate(string due) => SpecAssert.Check("due", DateOnly.FromDateTime(now).AddDays(3), due);
    }

    private readonly ClockGrammar _grammar = new();

    public RelativeTimeTests()
    {
        // Synchronously, in the test's own context: the clock is AsyncLocal.
        BobcatClock.ResetToControllable().SetUtcNow(new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero));
    }

    public void Dispose() => BobcatClock.Reset();

    [Fact]
    public void relative_tokens_resolve_against_the_frozen_clock()
    {
        var (recording, _) = Projected.Run("Relative tokens", () =>
        {
            _grammar.ClockDate("TODAY");
            _grammar.ReminderTime("NOW + 30 minutes");
            _grammar.DueDate("TODAY+3");
        });

        recording.Steps.ShouldAllBe(s => s.Status == ResultStatus.success);
    }

    [Fact]
    public void the_cell_shows_the_resolved_value_and_the_note_says_what_was_written()
    {
        var (recording, _) = Projected.Run("Relative token note", () => _grammar.DueDate("TODAY+3"));

        var cell = recording.Step("the computed due date should be").Cells.Single();
        cell.Expected.ShouldBe("2026-06-08");
        cell.Note.ShouldBe("TODAY+3");
        cell.DisplayText.ShouldBe("2026-06-08 (TODAY+3)");
    }

    [Fact]
    public void a_relative_token_can_disagree()
    {
        var (recording, _) = Projected.Run("Relative token disagrees", () => _grammar.DueDate("TODAY+4"));

        var cell = recording.Step("the computed due date should be").Cells.Single();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("2026-06-09");
        cell.Actual.ShouldBe("2026-06-08");
    }
}
