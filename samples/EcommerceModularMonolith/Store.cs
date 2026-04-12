using System.Collections.Concurrent;

namespace EcommerceModularMonolith;

// Domain models
public record Product(int Id, string Name, decimal Price, int Stock, string Category);
public record Customer(int Id, string Name, string Email, string Address);
public record OrderItem(int ProductId, string ProductName, int Qty, decimal UnitPrice);
public record Order(int Id, int CustomerId, List<OrderItem> Items, decimal Total, string Status);

// Request models
public record CreateProductRequest(string Name, decimal Price, int Stock, string Category);
public record CreateCustomerRequest(string Name, string Email, string Address);
public record PlaceOrderItemRequest(int ProductId, int Qty);
public record PlaceOrderRequest(int CustomerId, List<PlaceOrderItemRequest> Items);
public record UpdateStockRequest(int Adjustment);

public static class Store
{
    private static readonly ConcurrentDictionary<int, Product> _products = new();
    private static readonly ConcurrentDictionary<int, Customer> _customers = new();
    private static readonly ConcurrentDictionary<int, Order> _orders = new();
    private static int _nextProductId = 1;
    private static int _nextCustomerId = 1;
    private static int _nextOrderId = 1;

    public static void Reset()
    {
        _products.Clear();
        _customers.Clear();
        _orders.Clear();
        _nextProductId = 1;
        _nextCustomerId = 1;
        _nextOrderId = 1;
    }

    // Products
    public static Product CreateProduct(CreateProductRequest req)
    {
        var id = _nextProductId++;
        var product = new Product(id, req.Name, req.Price, req.Stock, req.Category);
        _products[id] = product;
        return product;
    }

    public static Product? GetProduct(int id) => _products.TryGetValue(id, out var p) ? p : null;
    public static IEnumerable<Product> GetAllProducts() => _products.Values.OrderBy(p => p.Id);

    public static Product? UpdateStock(int id, int adjustment)
    {
        if (!_products.TryGetValue(id, out var p)) return null;
        var updated = p with { Stock = p.Stock + adjustment };
        _products[id] = updated;
        return updated;
    }

    // Customers
    public static Customer CreateCustomer(CreateCustomerRequest req)
    {
        var id = _nextCustomerId++;
        var customer = new Customer(id, req.Name, req.Email, req.Address);
        _customers[id] = customer;
        return customer;
    }

    public static Customer? GetCustomer(int id) => _customers.TryGetValue(id, out var c) ? c : null;
    public static IEnumerable<Customer> GetAllCustomers() => _customers.Values.OrderBy(c => c.Id);

    // Orders
    public static (Order? Order, string? Error) PlaceOrder(PlaceOrderRequest req)
    {
        if (!_customers.ContainsKey(req.CustomerId))
            return (null, "Customer not found");

        var items = new List<OrderItem>();
        decimal total = 0;

        foreach (var item in req.Items)
        {
            if (!_products.TryGetValue(item.ProductId, out var product))
                return (null, $"Product {item.ProductId} not found");
            if (product.Stock < item.Qty)
                return (null, $"Insufficient stock for product {item.ProductId}");
            items.Add(new OrderItem(product.Id, product.Name, item.Qty, product.Price));
            total += product.Price * item.Qty;
        }

        // Decrement stock
        foreach (var item in req.Items)
        {
            var product = _products[item.ProductId];
            _products[item.ProductId] = product with { Stock = product.Stock - item.Qty };
        }

        var id = _nextOrderId++;
        var order = new Order(id, req.CustomerId, items, total, "pending");
        _orders[id] = order;
        return (order, null);
    }

    public static Order? GetOrder(int id) => _orders.TryGetValue(id, out var o) ? o : null;
    public static IEnumerable<Order> GetAllOrders() => _orders.Values.OrderBy(o => o.Id);
    public static IEnumerable<Order> GetOrdersForCustomer(int customerId) =>
        _orders.Values.Where(o => o.CustomerId == customerId).OrderBy(o => o.Id);

    public static Order? UpdateOrderStatus(int id, string newStatus)
    {
        if (!_orders.TryGetValue(id, out var order)) return null;
        var updated = order with { Status = newStatus };
        _orders[id] = updated;
        return updated;
    }
}
