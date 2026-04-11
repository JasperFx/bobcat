using Bobcat;

namespace ConsolePreview;

public record LineItem(string Description, int Quantity, decimal UnitPrice);

public class InvoicingFixture : Fixture
{
    private readonly List<LineItem> _items = new();

    public override Task SetUp()
    {
        _items.Clear();
        return Task.CompletedTask;
    }

    [Given("the following line items")]
    [Table]
    public void AddLineItem(string description, int quantity, decimal unitPrice)
    {
        _items.Add(new LineItem(description, quantity, unitPrice));
    }

    [When("the invoice is totaled")]
    public void TheInvoiceIsTotaled()
    {
        // The totaling happens lazily via properties below
    }

    [Then("the subtotal should be {decimal}")]
    public void TheSubtotalShouldBe(decimal expected)
    {
        var actual = _items.Sum(i => i.Quantity * i.UnitPrice);
        if (actual != expected)
            throw new Exception($"Expected subtotal {expected:F2} but got {actual:F2}");
    }

    [Then("the item count should be {int}")]
    public void TheItemCountShouldBe(int expected)
    {
        if (_items.Count != expected)
            throw new Exception($"Expected {expected} items but got {_items.Count}");
    }
}
