using Bobcat;
using Bobcat.Wolverine;

namespace OutboxDemo;

[FixtureTitle("Order Processing via Message Bus")]
public class OrderFixture : Fixture
{
    private Order? _lastOrder;

    [When("I place an order for {string} quantity {int} at price {decimal}")]
    public async Task PlaceOrder(string productName, int quantity, decimal price)
    {
        var session = await Context.InvokeMessageAndWaitAsync(
            new PlaceOrder(productName, quantity, price));

        if (session.Status != Wolverine.Tracking.TrackingStatus.Completed)
            throw new Exception($"Message session did not complete: {session.Status}");

        _lastOrder = OrderStore.GetAll().LastOrDefault(o => o.ProductName == productName);
    }

    [Then("the order count is {int}")]
    public void OrderCountIs(int expected)
    {
        var actual = OrderStore.GetAll().Count;
        if (actual != expected)
            throw new Exception($"Expected {expected} orders but got {actual}.");
    }

    [Then("the order product is {string}")]
    public void OrderProductIs(string expected)
    {
        if (_lastOrder?.ProductName != expected)
            throw new Exception($"Expected product '{expected}' but got '{_lastOrder?.ProductName}'.");
    }

    [Then("the inventory reduction for {string} is {int}")]
    public void InventoryReductionIs(string productName, int expected)
    {
        var actual = InventoryStore.GetReduced(productName);
        if (actual != expected)
            throw new Exception($"Expected inventory reduction of {expected} for '{productName}' but got {actual}.");
    }

    [Then("the order total is {decimal}")]
    public void OrderTotalIs(decimal expected)
    {
        if (_lastOrder?.Total != expected)
            throw new Exception($"Expected total {expected} but got {_lastOrder?.Total}.");
    }
}
