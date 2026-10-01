using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class SetVerificationTests
{
    [Fact]
    public async Task an_unordered_set_ignores_the_order_the_rows_arrive_in()
    {
        var results = await Specs.Run(Set_Verification_Feature.Define(),
            "An unordered set does not care what order the rows arrive in");

        results.Step("the details should be").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task an_ordered_set_passes_when_the_order_agrees()
    {
        var results = await Specs.Run(Set_Verification_Feature.Define(),
            "An ordered set passes when the order agrees");

        results.Step("the details in order should be").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task an_ordered_set_reports_the_row_that_turned_up_early()
    {
        var results = await Specs.Run(Set_Verification_Feature.Define(),
            "An ordered set reports the row that turned up early");
        var step = results.Step("the details in order should be");

        step.StepStatus.ShouldBe(ResultStatus.failed);

        var marker = step.Cells.Single(c => c.Name == SetVerificationComparer.OutOfOrderCell);
        marker.RowIndex.ShouldBe(1);
        marker.DisplayText.ShouldContain("found at position 1");

        // The row's own values are right — it is the position that disagrees.
        step.Cells.Where(c => c.RowIndex == 1 && c.Name != SetVerificationComparer.OutOfOrderCell)
            .ShouldAllBe(c => c.Status == ResultStatus.success);

        var render = SetVerificationRender.FromStepResult(step);
        render.Rows[1].RowType.ShouldBe(SetVerificationRowType.OutOfOrder);
    }

    [Fact]
    public async Task a_set_of_plain_values_is_compared_under_the_column_the_fixture_names()
    {
        var results = await Specs.Run(Set_Verification_Feature.Define(),
            "A set of plain values is compared under the column the fixture names");
        var step = results.Step("the names in order should be");

        step.SetVerificationColumns.ShouldBe(new[] { "Name" });

        // Luke is in place; Chewie and Han are swapped, so the second of them is the one reported.
        step.Cells.Single(c => c.RowIndex == 0 && c.Name == "Name").Status.ShouldBe(ResultStatus.success);
        step.Cells.Single(c => c.Name == SetVerificationComparer.OutOfOrderCell).RowIndex.ShouldBe(2);

        // Not compared against the properties of `string`, which is what happened without Column.
        step.Cells.ShouldNotContain(c => c.Name == "Length");
    }

    [Fact]
    public async Task a_column_can_carry_a_header_of_its_own()
    {
        var results = await Specs.Run(Set_Verification_Feature.Define(),
            "A column carries a header of its own");

        results.Step("the roster reads").StepStatus.ShouldBe(ResultStatus.success);

        // The grid shows the document's own heading, not the parameter name.
        results.Step("the roster is").SetVerificationColumns.ShouldBe(new[] { "Player Name", "grade" });
    }

    [Fact]
    public async Task an_optional_column_may_be_left_out_of_the_table()
    {
        var results = await Specs.Run(Set_Verification_Feature.Define(),
            "An optional column may be left out of the table");

        // The parameter's own default applies. Before this the argument was default(T), so an
        // `= Grade.Bronze` in the fixture was silently ignored and Gold (the enum's first member)
        // arrived instead.
        results.Step("the roster reads").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task a_header_with_no_rows_under_it_still_names_the_columns()
    {
        var results = await Specs.Run(Set_Verification_Feature.Define(),
            "A table with a header and no rows says the set is empty");
        var step = results.Step("the details should be");

        step.StepStatus.ShouldBe(ResultStatus.failed);

        // The header row says what the columns are even with nothing under it. Read off the first
        // expected row — which is what the generated path used to do — there is no first row, and the
        // one extra row rendered under no headings at all.
        step.SetVerificationColumns.ShouldBe(new[] { "Name", "Amount" });

        var extra = SetVerificationRender.FromStepResult(step).Rows.Single();
        extra.RowType.ShouldBe(SetVerificationRowType.Extra);
        extra.Cells.Select(c => c.DisplayText).ShouldBe(new[] { "Cord", "100" });
    }

    [Fact]
    public async Task a_property_titled_for_the_document_is_compared_under_its_title()
    {
        var results = await Specs.Run(Set_Verification_Feature.Define(),
            "A property titled for the document is compared under its title");
        var step = results.Step("the ledger should be");

        step.StepStatus.ShouldBe(ResultStatus.success);
        step.SetVerificationColumns.ShouldBe(new[] { "Line Item", "The Amount" });

        // Matched on "Line Item", so KeyColumns named the column the document writes.
        step.Cells.Where(c => c.Name == "The Amount").Select(c => c.Actual).ShouldBe(new[] { "200", "100" });
    }

    [Fact]
    public async Task a_title_replaces_the_property_name_rather_than_aliasing_it()
    {
        var results = await Specs.Run(Set_Verification_Feature.Define(),
            "A titled column is not also known by the property name");
        var step = results.Step("the ledger should be");

        // The cost of titling a column, stated: the property name now matches nothing, so the row is
        // missing and the real one is extra. One column with two spellings would be two columns, and
        // adding a header is a change to the vocabulary of every document that compares it.
        step.StepStatus.ShouldBe(ResultStatus.failed);
        SetVerificationRender.FromStepResult(step).Rows.Select(r => r.RowType)
            .ShouldBe(new[] { SetVerificationRowType.Missing, SetVerificationRowType.Extra });
    }
}
