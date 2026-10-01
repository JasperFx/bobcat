using Bobcat;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// One grammar, written in Storyteller's <c>[FormatAs]</c> syntax, matched against a
/// <c>.feature</c> file — the half of the attribute merge that had never been possible.
/// </summary>
/// <remarks>
/// <para>
/// Every expression here names PARAMETERS rather than Cucumber types: <c>{value}</c>, not
/// <c>{double}</c>. The parameter's own declared type is what decides how the cell is read, so the
/// step text says what the value <i>is</i>, and the very same attribute renders the sentence when a
/// projected xUnit test calls the method directly.
/// </para>
/// <para>
/// <b>Nothing here is a second vocabulary.</b> <c>{int}</c> and friends still work, in the same
/// expression if you like (<see cref="OperandCount"/>), and the built-in word wins so no expression
/// that compiled before means anything different now.
/// </para>
/// </remarks>
public class NamedTemplatesFixture : Fixture
{
    private readonly Calculator _calculator = new();

    /// <summary>The system under test, small enough to keep local to this fixture.</summary>
    public class Calculator
    {
        public double Value { get; set; }

        public void MultiplyBy(double value) => Value *= value;
    }
    private string _label = "Hello there World";

    [Given("Start with {value}")]
    public void StartWith(double value) => _calculator.Value = value;

    [When("Multiply by {value}")]
    public void MultiplyBy(double value) => _calculator.MultiplyBy(value);

    [Then("The value should be {value}")]
    public double TheValueShouldBe() => _calculator.Value;

    /// <summary>
    /// The placeholders are in the OPPOSITE order to the parameters. A Cucumber expression binds
    /// positionally and would silently swap them; a named template binds by name, so
    /// "5 added to 3" is 3 + 5 with <c>x = 3</c>, which is what the sentence says.
    /// </summary>
    [Then("{y} added to {x} should be {sum}")]
    public double Adding(double x, double y) => x + y;

    /// <summary>
    /// No keyword at all, and the feature file uses it under Given, When and Then — the document
    /// decides, which is where that decision belongs.
    /// </summary>
    [Step("Reset the calculator")]
    public void Reset() => _calculator.Value = 0;

    /// <summary>
    /// Both syntaxes in one expression: <c>{int}</c> is a Cucumber word, captured positionally, and
    /// <c>{label}</c> names a parameter. Deliberately allowed — it is a natural thing to write and
    /// there is no ambiguity in it, because each placeholder is resolved on its own.
    /// </summary>
    /// <remarks>
    /// Not <c>{type}</c>, which would have been the neater illustration: a type-name capture makes
    /// the scenario an Event Model slice claim, and <c>SpecIdentityAuditTests</c> rightly reported the
    /// new scenario as an orphan of a model nobody declared. Worth knowing — a capture word can have
    /// consequences a long way from the step it is in.
    /// </remarks>
    [Then("the calculator has {int} operands and the label {label}")]
    public bool OperandCountAndLabel(int count, string label) => count == 2 && label == _label;

    /// <summary>
    /// One sentence, several assertions, on an <c>async</c> method — a named tuple where Storyteller
    /// needed <c>out</c> parameters, which an <c>async</c> method cannot have at all.
    /// </summary>
    /// <remarks>
    /// Each element is compared against the cell that names it, and each is judged on its own: a wrong
    /// product must not hide a right sum. The elements' expected values are read in DECLARATION order
    /// however the sentence orders the placeholders, which is the same rule the parameters follow.
    /// </remarks>
    [Then("For {x} and {y}, the Sum should be {sum} and the Product should be {product}")]
    public async Task<(int Sum, int Product)> SumAndProduct(int x, int y)
    {
        await Task.Delay(1);
        return (x + y, x * y);
    }

    /// <summary>
    /// Storyteller's Fact, unchanged: a <c>bool</c>-returning step whose answer is the verdict and
    /// whose return value is not in the sentence. No <c>[Check]</c> needed.
    /// </summary>
    [Then("the calculator is at zero")]
    public bool IsAtZero() => _calculator.Value == 0;

    /// <summary>The asynchronous Fact, which Storyteller had no equivalent of.</summary>
    [Then("the ledger balances")]
    public async Task<bool> LedgerBalances()
    {
        await Task.Delay(1);
        return false;
    }

    /// <summary>
    /// A named placeholder over a <c>string</c> parameter takes the rest of the sentence, because the
    /// expression is anchored at both ends. A Cucumber <c>{string}</c> would have required quotes.
    /// </summary>
    [Then("the label should read {label}")]
    public string TheLabelShouldRead() => _label;
}
