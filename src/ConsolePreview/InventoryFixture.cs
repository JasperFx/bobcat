using Bobcat;

namespace ConsolePreview;

public record InventoryItem(string Sku, string ProductName, int Quantity);

public class InventoryFixture : Fixture
{
    private readonly Dictionary<string, InventoryItem> _inventory = new();

    public override Task SetUp()
    {
        _inventory.Clear();
        return Task.CompletedTask;
    }

    [Given("the warehouse is empty")]
    public void TheWarehouseIsEmpty()
    {
        _inventory.Clear();
    }

    [Given("the following shipment is received")]
    [Table]
    public void ReceiveShipment(string sku, string productName, int quantity)
    {
        if (_inventory.TryGetValue(sku, out var existing))
        {
            _inventory[sku] = existing with { Quantity = existing.Quantity + quantity };
        }
        else
        {
            _inventory[sku] = new InventoryItem(sku, productName, quantity);
        }
    }

    [When("{int} units of {string} are sold")]
    public void UnitsAreSold(int quantity, string sku)
    {
        if (!_inventory.TryGetValue(sku, out var item))
            throw new Exception($"SKU {sku} not found in inventory");

        if (item.Quantity < quantity)
            throw new Exception($"Insufficient inventory for {sku}: have {item.Quantity}, need {quantity}");

        _inventory[sku] = item with { Quantity = item.Quantity - quantity };
    }

    [Then("the inventory should be")]
    [SetVerification(KeyColumns = "Sku")]
    public IEnumerable<InventoryItem> TheInventoryShouldBe()
    {
        return _inventory.Values.OrderBy(i => i.Sku);
    }
}
