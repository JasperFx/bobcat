using Bobcat.Xunit.Samples.Application;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// Storyteller 5's <c>CalculatorFixture</c>, recreated as a projected-test grammar: one
/// <c>[Given]</c>/<c>[When]</c>/<c>[Then]</c> method per sentence, called from an ordinary xUnit test.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mapping, grammar for grammar.</b> <c>[FormatAs("Start with {value}")]</c> becomes
/// <c>[Given("Start with {value}")]</c> — the SAME named-template syntax, because Bobcat's step
/// attributes now accept it alongside Cucumber expressions. The placeholder binds to the parameter of
/// that name, at a C# call site and in a <c>.feature</c> file alike. The only thing added is the
/// <b>keyword</b>, which is optional: <c>SentenceGrammar</c> next door spells none.
/// </para>
/// <para>
/// <b>What changes for an assertion sentence.</b> Storyteller's value checks RETURN the actual
/// value and let the engine compare it against the expected cell in the specification:
/// </para>
/// <code>
/// [FormatAs("The value should be {value}")]
/// public double TheValueShouldBe() => _calculator.Value;
/// </code>
/// <para>
/// A projected test has no specification file, so the expected value arrives the only way anything
/// arrives in C#: as an argument. The helper therefore takes the expectation and reports the
/// comparison itself through <see cref="SpecAssert"/>. One extra line per assertion grammar, and
/// the cell it produces is the same structured expected/actual pair the Gherkin lane's
/// return-value verification produces.
/// </para>
/// <para>
/// <b>Why the methods are <c>internal</c>, not <c>private</c>.</b> The generated interceptor is an
/// extension method — a constraint of C#'s interceptor feature, not a choice — and an extension
/// method cannot reach a private or protected member. That is the whole adoption cost on an
/// existing suite: one keyword per helper.
/// </para>
/// </remarks>
public class CalculatorGrammar
{
    private readonly Calculator _calculator = new();

    /// <summary>Storyteller: <c>[FormatAs("Start with {value}")]</c> — a plain action sentence.</summary>
    [Given("Start with {value}")]
    internal void StartWith(double value) => _calculator.Value = value;

    [When("Add {value}")]
    internal void Add(double value) => _calculator.Add(value);

    [When("Subtract {value}")]
    internal void Subtract(double value) => _calculator.Subtract(value);

    [When("Multiply by {value}")]
    internal void MultiplyBy(double value) => _calculator.MultiplyBy(value);

    [When("Divide by {value}")]
    internal void DivideBy(double value) => _calculator.DivideBy(value);

    /// <summary>
    /// Storyteller's assertion sentence — <c>[FormatAs("The value should be {value}")]</c> over a
    /// <c>double</c>-returning method.
    /// </summary>
    [Then("The value should be {value}")]
    internal void TheValueShouldBe(double value) => SpecAssert.Check("value", _calculator.Value, value);

    /// <summary>
    /// Storyteller's multi-input assertion sentence — <c>[FormatAs("Adding {x} to {y} should equal
    /// {returnValue}")]</c>. The last placeholder was the return value's cell; here it is simply
    /// the last parameter.
    /// </summary>
    [Then("Adding {x} to {y} should equal {returnValue}")]
    internal void AddingNumbersTogether(double x, double y, double returnValue)
    {
        _calculator.Value = x;
        _calculator.Add(y);

        SpecAssert.Check("returnValue", _calculator.Value, returnValue);
    }

    /// <summary>
    /// Storyteller's output-parameter sentence: <b>several assertions in one sentence</b>
    /// (<c>void SumAndProduct(int X, int Y, out int Sum, out int Product)</c>). Two cells, each
    /// judged on its own, which is the whole point — a wrong product must not hide a right sum.
    /// </summary>
    /// <remarks>
    /// C# <c>out</c> parameters are deliberately NOT used: the caller would have to declare two
    /// locals it never reads, and in a projected test the expected values are the inputs. Bobcat's
    /// Gherkin lane still binds real <c>out</c> parameters, which is where that shape belongs.
    /// </remarks>
    [Then("For X={x} and Y={y}, the Sum should be {sum} and the Product should be {product}")]
    internal void SumAndProduct(int x, int y, int sum, int product)
    {
        SpecAssert.Check("Sum", x + y, sum);
        SpecAssert.Check("Product", x * y, product);
    }

    /// <summary>
    /// Storyteller's <c>Throw()</c> — an action sentence that blows up. Rendered as an
    /// <b>error</b>, not a wrong, and it stops the scenario: nothing after it can be trusted.
    /// </summary>
    [When("Divide by zero using integer maths")]
    internal void DivideByZero()
    {
        var zero = 0;
        _ = 1 / zero;
    }
}
