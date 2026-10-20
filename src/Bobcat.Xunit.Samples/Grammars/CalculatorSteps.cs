using Bobcat.Xunit.Samples.Application;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// The grammar <c>docs/getting-started.md</c> writes first: <see cref="CalculatorGrammar"/> cut down
/// to the four steps its one specification uses, with no commentary, so the page can include it
/// whole. The full grammar next door is the reference.
/// </summary>
// #region calculator-steps
public class CalculatorSteps
{
    private readonly Calculator _calculator = new();

    [Given("Start with {value}")]
    internal void StartWith(double value) => _calculator.Value = value;

    [When("Multiply by {value}")]
    internal void MultiplyBy(double value) => _calculator.MultiplyBy(value);

    [Then("The value should be {value}")]
    internal void TheValueShouldBe(double value) => SpecAssert.Check("value", _calculator.Value, value);

    [Then("Adding {x} to {y} should equal {returnValue}")]
    internal void AddingNumbersTogether(double x, double y, double returnValue)
    {
        _calculator.Value = x;
        _calculator.Add(y);
        SpecAssert.Check("returnValue", _calculator.Value, returnValue);
    }
}
// #endregion calculator-steps
