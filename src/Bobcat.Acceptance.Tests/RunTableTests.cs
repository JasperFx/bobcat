using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class RunTableTests
{
    private static async Task<(ExecutionResults Results, RunTableFixture Fixture)> run(string scenario)
    {
        var fixture = new RunTableFixture();
        var results = await Specs.Run(Run_Table_Feature.Define(), scenario, fixture);
        return (results, fixture);
    }

    [Fact]
    public async Task runs_the_named_method_once_per_row_and_renders_one_grid()
    {
        var (results, fixture) = await run("A table run through the fixture's own method");
        var step = results.Step("the members are");

        step.StepStatus.ShouldBe(ResultStatus.success);
        fixture.Flushed.ShouldBe("Ada:Pro|Grace:Enterprise");

        // One step, one grid — the same shape a generated [Table] step produces.
        step.IsSetVerification.ShouldBeTrue();
        step.SetVerificationColumns.ShouldBe(new[] { "Member Name", "tier" });
        SetVerificationRender.FromStepResult(step).Rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_header_renames_the_column_and_an_optional_column_may_be_left_out()
    {
        var (_, fixture) = await run("An optional column may be left out");

        // [Header("Member Name")] bound the column, and `tier = Tier.Free` supplied the rest.
        fixture.Flushed.ShouldBe("Ada:Free");
    }

    [Fact]
    public async Task a_row_whose_cell_will_not_convert_fails_that_row_alone()
    {
        var (results, fixture) = await run("A row whose cell will not convert fails that row alone");
        var step = results.Step("the members are");

        step.StepStatus.ShouldBe(ResultStatus.failed);

        var rowError = step.Cells.Single(c => c.Name == DecisionTableComparer.RowErrorCell);
        rowError.RowIndex.ShouldBe(1);
        rowError.DisplayText.ShouldContain("Free, Pro, Enterprise");

        // The rows either side of it still ran.
        fixture.Flushed.ShouldBe("Ada:Pro|Linus:Free");
    }

    [Fact]
    public async Task an_async_row_method_is_awaited_before_the_next_row()
    {
        var (_, fixture) = await run("An async row method is awaited");

        fixture.Log.ShouldBe(new[] { "Ada", "Grace" });
    }

    [Fact]
    public async Task a_returned_value_and_one_unclaimed_column_is_a_decision_table()
    {
        var (results, _) = await run("A decision table run by the fixture");
        var step = results.Step("doubling gives");

        step.StepStatus.ShouldBe(ResultStatus.failed);

        step.Cells.Single(c => c.RowIndex == 0 && c.Name == "doubled").Status
            .ShouldBe(ResultStatus.success);

        var wrong = step.Cells.Single(c => c.RowIndex == 1 && c.Name == "doubled");
        wrong.Status.ShouldBe(ResultStatus.failed);
        wrong.Expected.ShouldBe("7");
        wrong.Actual.ShouldBe("6");
    }

    [Fact]
    public async Task builds_one_object_per_row_with_relative_dates_and_declared_defaults()
    {
        var (results, fixture) = await run("A table of objects, with a relative date and a defaulted column");

        fixture.Built.Length.ShouldBe(2);
        fixture.Built[0].Customer.ShouldBe("Ada");
        fixture.Built[0].Tier.ShouldBe(Tier.Pro);

        // Relative to each other, because TODAY is a fact about the run.
        fixture.Built[0].RenewsOn.ShouldBe(fixture.Built[1].RenewsOn.AddDays(30));

        // The Currency column is not in the table at all; the record's own default supplied it.
        fixture.Built.ShouldAllBe(s => s.Currency == "USD");

        results.Step("the subscriptions are").IsSetVerification.ShouldBeTrue();
    }

    [Fact]
    public void a_method_name_that_matches_nothing_says_so()
    {
        var fixture = new RunTableFixture();

        Should.Throw<SpecCriticalException>(() => TableRunner.MethodNamed(fixture, "noSuchMethod"))
            .Message.ShouldContain("no method named 'noSuchMethod'");
    }
}
