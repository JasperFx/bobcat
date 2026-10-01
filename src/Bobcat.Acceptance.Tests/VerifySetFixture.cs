using Bobcat;

namespace Bobcat.Acceptance.Tests;

public record InventoryItem(string Sku, string ProductName, int Quantity);

/// <summary>
/// A set verification the step makes itself — <c>VerifySet</c> over a <see cref="StepTable"/>
/// parameter — rather than one <c>[SetVerification]</c> declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>This fixture is driven from both lanes, and that is what it exists to prove.</b>
/// <c>VerifySetTests</c> runs it through the generated feature; <c>ProjectedVerifySetTests</c>
/// constructs it and calls the same methods with table literals. One grammar body, one comparison,
/// one grid — which is reachable only because the expected rows arrive as an argument. A
/// <c>[SetVerification]</c> method gets them from the generator and so can never be called from C#.
/// </para>
/// </remarks>
public class VerifySetFixture : Fixture
{
    private readonly List<InventoryItem> _inventory = new();
    private readonly List<string> _tags = new();

    public void BeforeEach()
    {
        _inventory.Clear();
        _tags.Clear();
    }

    /// <summary>The arrange is the table's twin: <c>BuildRows</c> in, <c>VerifySet</c> out.</summary>
    [Given("the inventory is")]
    public void TheInventoryIs(StepTable table) => _inventory.AddRange(BuildRows<InventoryItem>(table));

    [Then("the inventory should be")]
    public void TheInventoryShouldBe(StepTable expected)
        => VerifySet(_inventory, expected, keyColumns: "Sku");

    [Then("the inventory in order should be")]
    public void TheInventoryInOrderShouldBe(StepTable expected)
        => VerifySet(_inventory, expected, keyColumns: "Sku", ordered: true);

    [Given("the tags are")]
    public void TheTagsAre(StepTable table) => _tags.AddRange(table.Rows.Select(r => r[0]));

    /// <summary>
    /// A set of plain values with no column named: the table has one column, so there is only one
    /// thing it could be. <c>[SetVerification(Column = "…")]</c> has to be told, because BOBCAT031
    /// is decided at compile time with no table in view.
    /// </summary>
    [Then("the tags should be")]
    public void TheTagsShouldBe(StepTable expected) => VerifySet(_tags, expected);
}
