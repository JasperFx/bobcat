using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// The attribute merge, proved end to end against the real generator: Storyteller's
/// <c>[FormatAs]</c> syntax on <c>[Given]/[When]/[Then]/[Step]</c>, matched from a
/// <c>.feature</c> file.
/// </summary>
public class NamedTemplateTests
{
    [Fact]
    public async Task a_named_template_binds_by_parameter_name()
    {
        var results = await Specs.Run(Named_Templates_Feature.Define(), "A named template binds by parameter name");
        results.Counts.Succeeded.ShouldBeTrue();

        var step = results.Step("The value should be 6");
        step.Cells.Single().Actual.ShouldBe("6");
    }

    [Fact]
    public async Task placeholders_may_be_written_out_of_parameter_order()
    {
        // "5 added to 3" over Adding(double x, double y) is 3 + 5. A Cucumber expression binds
        // positionally and would have silently swapped them; both happen to be 8 here, so the
        // assertion that matters is the CELLS, not the sum.
        var results = await Specs.Run(
            Named_Templates_Feature.Define(), "Placeholders may be written out of parameter order");

        results.Counts.Succeeded.ShouldBeTrue();
        results.Step("5 added to 3 should be 8").Cells.Single().Expected.ShouldBe("8");
    }

    [Fact]
    public async Task a_named_template_can_disagree()
    {
        var results = await Specs.Run(Named_Templates_Feature.Define(), "A named template can disagree");

        var cell = results.Step("The value should be 7").Cells.Single();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("7");
        cell.Actual.ShouldBe("3");
    }

    [Fact]
    public async Task a_keywordless_step_matches_under_any_keyword()
    {
        // Storyteller and Gauge sentences carry no keyword. [Step] matches whatever the document
        // wrote — the same rule [TableGrammar] has always followed.
        var results = await Specs.Run(
            Named_Templates_Feature.Define(), "A keywordless step matches under any keyword");

        results.Counts.Succeeded.ShouldBeTrue();
        results.Steps.Count(x => x.StepText == "Reset the calculator").ShouldBe(2);
    }

    [Fact]
    public async Task cucumber_and_named_placeholders_mix_in_one_expression()
    {
        // Deliberately allowed: each placeholder resolves on its own, so there is nothing ambiguous
        // about "the {type} type has {operands} operands".
        var results = await Specs.Run(
            Named_Templates_Feature.Define(), "Cucumber and named placeholders mix in one expression");

        results.Counts.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task a_named_string_placeholder_may_carry_spaces()
    {
        // A Cucumber {string} requires quotes. A named placeholder over a string parameter takes the
        // rest of the sentence, because the expression is anchored at both ends.
        var results = await Specs.Run(
            Named_Templates_Feature.Define(), "A named string placeholder may carry spaces");

        results.Counts.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task a_tuple_return_is_compared_element_by_element()
    {
        var results = await Specs.Run(
            Named_Templates_Feature.Define(), "A tuple return is compared element by element");

        results.Counts.Succeeded.ShouldBeTrue();

        var cells = results.Step("For 3 and 4, the Sum should be 7 and the Product should be 12").Cells;
        cells.Select(x => x.Name).ShouldBe(["Sum", "Product"]);
        cells.ShouldAllBe(x => x.Status == ResultStatus.success);
    }

    [Fact]
    public async Task one_tuple_element_may_be_wrong_while_the_other_is_right()
    {
        // The whole reason each element gets its own cell: Storyteller's out-parameter sentence made
        // several claims, and a wrong product must not hide a right sum.
        var results = await Specs.Run(
            Named_Templates_Feature.Define(), "One tuple element may be wrong while the other is right");

        var cells = results.Step("For 4 and 4, the Sum should be 8 and the Product should be 15").Cells;
        cells.Single(x => x.Name == "Sum").Status.ShouldBe(ResultStatus.success);

        var product = cells.Single(x => x.Name == "Product");
        product.Status.ShouldBe(ResultStatus.failed);
        product.Expected.ShouldBe("15");
        product.Actual.ShouldBe("16");
    }

    [Fact]
    public async Task a_bool_return_is_the_verdict()
    {
        // Storyteller's Fact, with no [Check] anywhere. Before this a [Then] returning bool fell
        // through to the plain action path and the answer was DISCARDED — a step that could not fail.
        var results = await Specs.Run(Named_Templates_Feature.Define(), "A bool return is the verdict");
        results.Counts.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task a_bool_return_can_be_false()
    {
        var results = await Specs.Run(Named_Templates_Feature.Define(), "A bool return can be false");

        results.Counts.Succeeded.ShouldBeFalse();
        results.Step("the calculator is at zero").StepStatus.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public async Task an_asynchronous_fact_is_a_fact()
    {
        // Task<bool>. Storyteller had no equivalent — it was written before async/await was common,
        // which is also why its multi-assertion sentence had to use `out`.
        var results = await Specs.Run(Named_Templates_Feature.Define(), "An asynchronous fact");
        results.Step("the ledger balances").StepStatus.ShouldBe(ResultStatus.failed);
    }
}
