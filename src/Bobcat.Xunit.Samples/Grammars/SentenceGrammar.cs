namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// Storyteller 5's <c>SentenceFixture</c> — and the point of it here is that <b>not one of these
/// steps spells a keyword</b>.
/// </summary>
/// <remarks>
/// <para>
/// Storyteller sentences read as prose, and so do Gauge's. <c>Keyword</c> on
/// <see cref="BobcatStepAttribute"/> is optional, and leaving it off is not a gap to be filled in
/// later: a sentence like "Multiply by 3 then add 4" is not a Given, a When or a Then, and forcing
/// one on it makes the specification read worse than the tool it replaced.
/// </para>
/// <para>
/// The two styles mix freely — <c>CalculatorGrammar</c> next door declares keywords throughout —
/// because the keyword is per step, not per suite.
/// </para>
/// </remarks>
public class SentenceGrammar
{
    private int _number;

    /// <summary>Storyteller: <c>[FormatAs("Start with the number {number}")]</c>.</summary>
    [Step("Start with the number {number}")]
    internal void StartWithTheNumber(int number = 5) => _number = number;

    [Step("Multiply by {multiplier} then add {delta}")]
    internal void MultiplyThenAdd(int multiplier, int delta)
    {
        _number *= multiplier;
        _number += delta;
    }

    [Step("Subtract {operand}")]
    internal void Subtract(int operand) => _number -= operand;

    [Step("Divide by {operand}")]
    internal void DivideBy(int operand) => _number /= operand;

    /// <summary>Storyteller's <c>[return: AliasAs("number")]</c> value check.</summary>
    [Step("The number should now be {number}")]
    internal void TheValueShouldBe(int number) => SpecAssert.Check("number", _number, number);

    /// <summary>Storyteller's multi-input value check, <c>TheSumOf</c>.</summary>
    [Step("The sum of {number1} and {number2} should be {sum}")]
    internal void TheSumOf(int number1, int number2, int sum)
        => SpecAssert.Check("sum", number1 + number2, sum);

    [Step("This line is always true")]
    internal bool ThisLineIsAlwaysTrue() => true;

    [Step("This line is always false")]
    internal bool ThisLineIsAlwaysFalse() => false;

    [Step("This line always throws exceptions")]
    internal void ThisLineAlwaysThrowsExceptions() => throw new NotImplementedException("No go!");

    /// <summary>
    /// Storyteller's <c>{x} + {y} should be {sum}</c> fact — a boolean grammar whose inputs are all
    /// in the sentence.
    /// </summary>
    [Step("{x} + {y} should be {sum}")]
    internal bool XplusYShouldBe(int x, int y, int sum) => x + y == sum;

    // A tuple-returning multi-assertion sentence lives in the GHERKIN lane — see
    // Bobcat.Acceptance.Tests/NamedTemplatesFixture. Written here it earns two BOBCAT027 warnings and
    // deserves them: {sum} and {product} are the EXPECTED cells, a feature file supplies them, and a
    // C# call site cannot. The step would render with its placeholders unresolved and pass regardless
    // — a specification that cannot fail.
}
