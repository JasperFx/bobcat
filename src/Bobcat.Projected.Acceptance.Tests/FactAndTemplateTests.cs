using Bobcat.Engine;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// The half of <c>NamedTemplates.feature</c> and <c>EditorVisibleCheck.feature</c> that is not
/// step matching: a named template renders by parameter name whatever order the sentence puts the
/// placeholders in, a keywordless <c>[Step]</c> reads as prose, and a <c>bool</c>-returning step
/// (sync or async, <c>[Then]</c> or <c>[Check]</c>) is its own verdict.
/// </summary>
/// <remarks>
/// <para>
/// These run through real interceptors (the project opts into <c>Bobcat.Generated</c>), which is the
/// only way to prove a bool's answer reaches the verdict: the generated text alone cannot say the
/// call was replaced. <c>Bobcat.Xunit.Samples</c> shows the same shapes but is never run by CI,
/// because its samples fail on purpose.
/// </para>
/// <para>
/// A tuple-returning multi-assertion sentence is Gherkin-only and dies with that lane: its expected
/// cells come from the document, and a C# call site has no way to supply them (BOBCAT027). The
/// projected form is several <c>SpecAssert.Check</c> calls, pinned in <see cref="ValueCheckTests"/>.
/// </para>
/// </remarks>
public class FactAndTemplateTests
{
    public class CalculatorGrammar
    {
        public double Value;

        [Given("Start with {value}")]
        internal void StartWith(double value) => Value = value;

        [When("Multiply by {value}")]
        internal void MultiplyBy(double value) => Value *= value;

        [Then("The value should be {value}")]
        internal void TheValueShouldBe(double value) => SpecAssert.Check("value", Value, value);

        /// <summary>The placeholders are in the opposite order to the parameters.</summary>
        [Then("{y} added to {x} should be {sum}")]
        internal void Adding(double x, double y, double sum) => SpecAssert.Check("sum", x + y, sum);

        [Step("Reset the calculator")]
        internal void Reset() => Value = 0;

        [Then("the label should read {label}")]
        internal void TheLabelShouldRead(string label) => SpecAssert.Check("label", "Hello there World", label);

        [Then("the calculator is at zero")]
        internal bool IsAtZero() => Value == 0;

        [Then("the ledger balances")]
        internal async Task<bool> LedgerBalances()
        {
            await Task.Delay(1);
            return false;
        }

        [Check("the value is positive")]
        internal bool IsPositive() => Value > 0;

        [Then("the value is negative")]
        [Check("the value is negative")]
        internal bool IsNegative() => Value < 0;
    }

    private readonly CalculatorGrammar _grammar = new();

    [Fact]
    public void a_named_template_binds_and_renders_by_parameter_name()
    {
        var (recording, _) = Projected.Run("Named template", () =>
        {
            _grammar.StartWith(3);
            _grammar.MultiplyBy(2);
            _grammar.TheValueShouldBe(6);
        });

        recording.Steps.Select(s => s.ToString()).ShouldBe(
        [
            "Given Start with 3",
            "When Multiply by 2",
            "Then The value should be 6"
        ]);
        recording.Step("The value should be 6").Cells.Single().Actual.ShouldBe("6");
        recording.GatheredFailures().ShouldBeNull();
    }

    [Fact]
    public void placeholders_render_by_name_whatever_order_the_sentence_puts_them_in()
    {
        // Adding(x: 3, y: 5): the sentence names y first, so it reads "5 added to 3". A positional
        // substitution would have swapped them, and with a sum both orders agree on, only the
        // rendered text can tell.
        var (recording, _) = Projected.Run("Out of order", () => _grammar.Adding(x: 3, y: 5, sum: 8));

        var step = recording.Steps.Single();
        step.Text.ShouldBe("5 added to 3 should be 8");
        step.Cells.Single().Expected.ShouldBe("8");
        step.Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void a_named_template_can_disagree()
    {
        var (recording, _) = Projected.Run("Named template disagrees", () =>
        {
            _grammar.StartWith(3);
            _grammar.TheValueShouldBe(7);
        });

        var cell = recording.Step("The value should be 7").Cells.Single();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("7");
        cell.Actual.ShouldBe("3");
    }

    [Fact]
    public void a_keywordless_step_reads_as_prose()
    {
        var (recording, _) = Projected.Run("Keywordless", () =>
        {
            _grammar.StartWith(3);
            _grammar.Reset();
            _grammar.TheValueShouldBe(0);
        });

        var reset = recording.Step("Reset the calculator");
        reset.Keyword.ShouldBe("");
        reset.ToString().ShouldBe("Reset the calculator");
        recording.GatheredFailures().ShouldBeNull();
    }

    [Fact]
    public void a_named_string_placeholder_may_carry_spaces()
    {
        var (recording, _) = Projected.Run("Spaces", () => _grammar.TheLabelShouldRead("Hello there World"));

        var step = recording.Steps.Single();
        step.Text.ShouldBe("the label should read Hello there World");
        step.Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void a_bool_return_is_the_verdict()
    {
        var (recording, _) = Projected.Run("Bool true", () =>
        {
            _grammar.StartWith(0);
            _grammar.IsAtZero().ShouldBeTrue();
        });

        recording.Step("the calculator is at zero").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void a_bool_return_can_be_false()
    {
        var (recording, _) = Projected.Run("Bool false", () =>
        {
            _grammar.StartWith(3);

            // The answer still reaches the caller untouched.
            _grammar.IsAtZero().ShouldBeFalse();
        });

        recording.Step("the calculator is at zero").Status.ShouldBe(ResultStatus.failed);
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("the calculator is at zero");
    }

    [Fact]
    public async Task an_asynchronous_fact_is_a_fact()
    {
        var (recording, _) = await Projected.RunAsync("Async fact", async () =>
        {
            (await _grammar.LedgerBalances()).ShouldBeFalse();
        });

        recording.Step("the ledger balances").Status.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public void a_check_is_a_fact_that_reads_as_then()
    {
        var (recording, _) = Projected.Run("Check", () =>
        {
            _grammar.StartWith(3);
            _grammar.IsPositive();
            _grammar.IsNegative();
        });

        var positive = recording.Step("the value is positive");
        positive.Keyword.ShouldBe("Then");
        positive.Status.ShouldBe(ResultStatus.success);

        // Stacked with a [Then] of the same text, it is still a check: the false answer fails it.
        recording.Step("the value is negative").Status.ShouldBe(ResultStatus.failed);
    }
}
