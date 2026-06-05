using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class ClockTests
{
    [Fact]
    public async Task freeze_date_and_advance_by_duration()
    {
        var results = await Specs.Run(Clock_Feature.Define(), "Freeze the date and advance by a duration");
        results.Step("the clock date should be").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task duration_passes_phrasing()
    {
        var results = await Specs.Run(Clock_Feature.Define(), "Advancing duration phrasing");
        results.Step("the clock date should be").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task advance_to_explicit_instant()
    {
        var results = await Specs.Run(Clock_Feature.Define(), "Advance to an explicit instant");
        results.Step("the clock date should be").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task relative_tokens_resolve_and_annotate_note()
    {
        var results = await Specs.Run(Clock_Feature.Define(), "Relative tokens resolve against the frozen clock");

        var due = results.Step("the computed due date should be");
        due.StepStatus.ShouldBe(ResultStatus.success);

        // The token and its resolved value are shown in the cell note.
        var cell = due.Cells.Single();
        cell.Note.ShouldBe("TODAY+3 → 2026-06-08");

        results.Step("the reminder time should be").StepStatus.ShouldBe(ResultStatus.success);
        results.Step("the clock date should be").StepStatus.ShouldBe(ResultStatus.success);
    }
}
