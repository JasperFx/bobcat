using Bobcat.Engine;
using Bobcat.Engine.Verification;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// The value checks <c>ReturnValue.feature</c> and <c>OutParams.feature</c> pin, from the projected
/// lane: a step helper takes the expectation as an argument and reports the comparison through
/// <see cref="SpecAssert"/>, rather than returning the actual value for the generator to compare.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes do not cross over, and that is deliberate rather than a gap. A projected helper does
/// not return the value it checks, because a C# call site has nowhere to write the expected cell; and
/// it does not use <c>out</c> parameters for several outputs, because the caller would declare locals
/// it never reads. One sentence with several claims is several <c>SpecAssert.Check</c> calls, each its
/// own cell.
/// </para>
/// <para>
/// The tolerance the removed Gherkin lane's <c>[Approx]</c> carried is a <see cref="CheckOptions"/>
/// argument, which lands as a note on the cell.
/// </para>
/// </remarks>
public class ValueCheckTests
{
    public class ValueGrammar
    {
        [Then("{a} plus {b} should be {sum}")]
        internal void Plus(int a, int b, int sum) => SpecAssert.Check("sum", a + b, sum);

        [Then("the average of {a} and {b} is {average}")]
        internal void Average(int a, int b, double average)
            => SpecAssert.Check("average", (a + b) / 2.0, average, new CheckOptions { Tolerance = 0.1 });

        [Then("the greeting for {name} should be {greeting}")]
        internal void Greeting(string name, string greeting) => SpecAssert.Check("greeting", $"Hello {name}", greeting);

        [When("dividing {dividend} by {divisor} gives {quotient} remainder {remainder}")]
        internal void DivMod(int dividend, int divisor, int quotient, int remainder)
        {
            SpecAssert.Check("quotient", dividend / divisor, quotient);
            SpecAssert.Check("remainder", dividend % divisor, remainder);
        }
    }

    private readonly ValueGrammar _grammar = new();

    [Fact]
    public void a_matching_value_is_a_structured_success_cell()
    {
        var (recording, _) = Projected.Run("Addition passes", () => _grammar.Plus(2, 3, 5));
        var step = recording.Step("2 plus 3 should be 5");

        step.Status.ShouldBe(ResultStatus.success);
        var cell = step.Cells.Single();
        cell.Name.ShouldBe("sum");
        cell.Expected.ShouldBe("5");
        cell.Actual.ShouldBe("5");
    }

    [Fact]
    public void a_mismatched_value_fails_with_expected_and_actual()
    {
        var (recording, _) = Projected.Run("Addition fails", () => _grammar.Plus(2, 3, 6));
        var step = recording.Step("2 plus 3 should be 6");

        step.Status.ShouldBe(ResultStatus.failed);
        var cell = step.Cells.Single();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("6");
        cell.Actual.ShouldBe("5");
        cell.DisplayText.ShouldBe("expected '6', got '5'");

        // A wrong is gathered, not thrown: the scenario carries on and settles its verdict at the end.
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("2 plus 3 should be 6");
    }

    [Fact]
    public void a_tolerance_flows_into_the_comparison_and_its_note()
    {
        var (recording, _) = Projected.Run("Approximate average", () => _grammar.Average(1, 2, 1.55));
        var step = recording.Step("the average of 1 and 2 is 1.55");

        step.Status.ShouldBe(ResultStatus.success);
        step.Cells.Single().Note.ShouldBe("±0.1");
    }

    [Fact]
    public void a_string_value_passes()
    {
        var (recording, _) = Projected.Run("String passes", () => _grammar.Greeting("World", "Hello World"));
        recording.Step("greeting").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void a_string_value_fails()
    {
        var (recording, _) = Projected.Run("String fails", () => _grammar.Greeting("World", "Goodbye World"));
        var cell = recording.Step("greeting").Cells.Single();

        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Actual.ShouldBe("Hello World");
        cell.Expected.ShouldBe("Goodbye World");
    }

    [Fact]
    public void several_outputs_of_one_sentence_are_one_cell_each()
    {
        var (recording, _) = Projected.Run("Both outputs correct", () => _grammar.DivMod(17, 5, 3, 2));
        var step = recording.Step("dividing 17 by 5");

        step.Status.ShouldBe(ResultStatus.success);
        step.Cells.Count.ShouldBe(2);
        step.Cells.ShouldContain(c => c.Name == "quotient" && c.Status == ResultStatus.success);
        step.Cells.ShouldContain(c => c.Name == "remainder" && c.Status == ResultStatus.success);
    }

    [Fact]
    public void one_wrong_output_fails_the_step_and_names_its_cell()
    {
        var (recording, _) = Projected.Run("One output wrong", () => _grammar.DivMod(17, 5, 3, 0));
        var step = recording.Step("dividing 17 by 5");

        step.Status.ShouldBe(ResultStatus.failed);
        var remainder = step.Cells.Single(c => c.Name == "remainder");
        remainder.Status.ShouldBe(ResultStatus.failed);
        remainder.Expected.ShouldBe("0");
        remainder.Actual.ShouldBe("2");

        // A wrong remainder does not hide a right quotient.
        step.Cells.Single(c => c.Name == "quotient").Status.ShouldBe(ResultStatus.success);
    }
}
