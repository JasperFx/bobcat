using Bobcat;

namespace Bobcat.Mtp.SampleHost;

/// <summary>
/// Features/Arithmetic.feature. A step method with a return value is compared against the last
/// placeholder in its text, which is what makes <c>subtraction disagrees</c> a comparison
/// FAILURE reporting <c>result: expected 4, got 5</c> rather than a thrown error.
/// </summary>
[FixtureTitle("Arithmetic")]
public class ArithmeticFixture : Fixture
{
    [Then("2 + 2 gives {int}")]
    public int Addition() => 2 + 2;

    [Then("9 - 4 gives {int}")]
    public int Subtraction() => 9 - 4;

    [When("dividing by zero")]
    public void DivisionExplodes()
        => throw new InvalidOperationException("attempted to divide by zero");
}

/// <summary>Features/Inventory.feature — carries the tags the filtering tests select on.</summary>
[FixtureTitle("Inventory")]
public class InventoryFixture : Fixture
{
    [Then("the stock is counted")]
    public void StockIsCounted() { }

    [Then("the restock completes")]
    public void RestockCompletes() { }
}
