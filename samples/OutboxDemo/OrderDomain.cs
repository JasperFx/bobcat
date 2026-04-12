using System.Collections.Concurrent;

namespace OutboxDemo;

// Messages
public record PlaceOrder(string ProductName, int Quantity, decimal UnitPrice);
public record UpdateInventory(string ProductName, int QuantityReduced);

// Domain model
public record Order(int Id, string ProductName, int Quantity, decimal UnitPrice, string Status)
{
    public decimal Total => Quantity * UnitPrice;
}

public record InventoryEntry(string ProductName, int TotalReduced);

// In-memory stores
public static class OrderStore
{
    private static readonly ConcurrentDictionary<int, Order> _orders = new();
    private static int _nextId = 1;

    public static void Reset()
    {
        _orders.Clear();
        _nextId = 1;
    }

    public static Order Add(string productName, int qty, decimal unitPrice)
    {
        var id = _nextId++;
        var order = new Order(id, productName, qty, unitPrice, "pending");
        _orders[id] = order;
        return order;
    }

    public static IReadOnlyList<Order> GetAll() => _orders.Values.OrderBy(o => o.Id).ToList();
    public static Order? GetById(int id) => _orders.TryGetValue(id, out var o) ? o : null;

    public static void UpdateStatus(int id, string status)
    {
        if (_orders.TryGetValue(id, out var o))
            _orders[id] = o with { Status = status };
    }
}

public static class InventoryStore
{
    private static readonly ConcurrentDictionary<string, int> _reductions = new();

    public static void Reset() => _reductions.Clear();

    public static void Record(string productName, int qty)
        => _reductions.AddOrUpdate(productName, qty, (_, existing) => existing + qty);

    public static int GetReduced(string productName)
        => _reductions.TryGetValue(productName, out var qty) ? qty : 0;
}

// Wolverine handlers
public class OrderHandler
{
    public UpdateInventory Handle(PlaceOrder command)
    {
        OrderStore.Add(command.ProductName, command.Quantity, command.UnitPrice);
        // Return a cascading message
        return new UpdateInventory(command.ProductName, command.Quantity);
    }
}

public class InventoryHandler
{
    public void Handle(UpdateInventory command)
    {
        InventoryStore.Record(command.ProductName, command.QuantityReduced);
    }
}
