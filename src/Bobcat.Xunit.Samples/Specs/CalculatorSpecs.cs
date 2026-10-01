using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// Storyteller 5's <c>Specs/Calculator</c> and <c>Specs/Assertions</c> suites, recreated as
/// projected xUnit tests.
/// </summary>
/// <remarks>
/// <para>
/// Each test below is one Storyteller <c>.md</c> specification. The originals are named in the
/// comment above each test, and several of them <b>fail on purpose</b> — that is what they were
/// written for. Storyteller's samples existed to show how each outcome renders, and so do these.
/// </para>
/// <para>
/// <b>What a reader should compare.</b> A Storyteller specification was a document with the steps
/// in it; this is a test method with the steps in it. The rendered output should be the same
/// document either way — that equivalence is the whole claim of the projected lane.
/// </para>
/// </remarks>
[BobcatFeature("Calculator"), BobcatScenario]
public class CalculatorSpecs
{
    private readonly CalculatorGrammar _calculator = new();

    /// <summary>Storyteller: <c>Specs/Calculator/Using_Sentences.md</c>. All green.</summary>
    [Fact]
    public void using_sentences()
    {
        _calculator.StartWith(3);
        _calculator.MultiplyBy(2);
        _calculator.TheValueShouldBe(6);
        _calculator.AddingNumbersTogether(2, 3, 5);
    }

    /// <summary>
    /// Storyteller: <c>Specs/Calculator/Bad_Values.md</c> — "This value is wrong and the
    /// specification should fail". One wrong cell, expected 7 and got 6.
    /// </summary>
    [Fact]
    public void bad_values()
    {
        _calculator.StartWith(3);
        _calculator.MultiplyBy(2);
        _calculator.TheValueShouldBe(7);
    }

    /// <summary>
    /// Storyteller: <c>Specs/Calculator/Using_Output_Parameters_in_a_Sentence.md</c> — one sentence
    /// making two assertions, both right.
    /// </summary>
    [Fact]
    public void using_output_parameters_in_a_sentence()
    {
        _calculator.SumAndProduct(3, 4, sum: 7, product: 12);
    }

    /// <summary>
    /// Storyteller: <c>Specs/Assertions/Asserting_Values.md</c> — and the one that matters most for
    /// rendering. The original's comment says "yes, I wrote the specification to fail on purpose",
    /// and it fails in the middle: the sum is right, the product is wrong, and the assertions AFTER
    /// the wrong one still run.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that would be impossible with a plain assertion library.</b> Shouldly or
    /// <c>Assert.Equal</c> would throw at the wrong product, and the last two steps would never run
    /// — so the report would show one failure and two blanks, rather than the four verdicts the
    /// specification actually reached. <see cref="SpecAssert"/> gathers instead of throwing, and the
    /// test is still reported red, once, at the end.
    /// </remarks>
    [Fact]
    public void asserting_values()
    {
        _calculator.AddingNumbersTogether(3, 3, 6);
        _calculator.SumAndProduct(5, 6, sum: 11, product: 30);

        // The original's decision table, row for row — and its second row is the one Storyteller
        // wrote wrong on purpose: X=4, Y=4 makes 8 and 16, not the 6 and 8 the specification claims.
        _calculator.SumAndProduct(3, 3, sum: 6, product: 9);
        _calculator.SumAndProduct(4, 4, sum: 6, product: 8);

        _calculator.TheValueShouldBe(6);
    }

    /// <summary>
    /// Storyteller: the <c>CriticalThrowing</c> shape. An exception from an ACTION is an error, not
    /// a wrong, and the steps after it never run — there is no sense in asserting against a
    /// calculator whose last operation blew up.
    /// </summary>
    [Fact]
    public void an_exception_in_an_action_stops_the_scenario()
    {
        _calculator.StartWith(3);
        _calculator.DivideByZero();

        // Never reached. Storyteller rendered the remaining steps as unexecuted; xUnit simply stops
        // here, which is the single most visible difference between a spec engine and a test method.
        _calculator.TheValueShouldBe(3);
    }
}
