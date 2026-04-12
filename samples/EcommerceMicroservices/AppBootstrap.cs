using System.Collections.Concurrent;

namespace EcommerceMicroservices;

public record Product(int Id, string Name, decimal Price, int Stock, string Category);
public record CreateProductRequest(string Name, decimal Price, int Stock, string Category);
public record UpdateProductRequest(string Name, decimal Price, int Stock, string Category);

public static class ProductStore
{
    private static readonly ConcurrentDictionary<int, Product> _products = new();
    private static int _nextId = 1;

    public static void Reset()
    {
        _products.Clear();
        _nextId = 1;
    }

    public static Product Create(CreateProductRequest req)
    {
        var id = _nextId++;
        var product = new Product(id, req.Name, req.Price, req.Stock, req.Category);
        _products[id] = product;
        return product;
    }

    public static Product? GetById(int id) => _products.TryGetValue(id, out var p) ? p : null;

    public static IEnumerable<Product> GetAll() => _products.Values.OrderBy(p => p.Id);

    public static IEnumerable<Product> GetByCategory(string category) =>
        _products.Values.Where(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Id);

    public static Product? Update(int id, UpdateProductRequest req)
    {
        if (!_products.ContainsKey(id)) return null;
        var updated = new Product(id, req.Name, req.Price, req.Stock, req.Category);
        _products[id] = updated;
        return updated;
    }

    public static bool Delete(int id) => _products.TryRemove(id, out _);
}

public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/products", (string? category) =>
        {
            if (!string.IsNullOrEmpty(category))
                return Results.Ok(ProductStore.GetByCategory(category));
            return Results.Ok(ProductStore.GetAll());
        });

        app.MapPost("/api/products", (CreateProductRequest req) =>
        {
            var product = ProductStore.Create(req);
            return Results.Created($"/api/products/{product.Id}", product);
        });

        app.MapGet("/api/products/{id:int}", (int id) =>
        {
            var product = ProductStore.GetById(id);
            return product is not null ? Results.Ok(product) : Results.NotFound();
        });

        app.MapPut("/api/products/{id:int}", (int id, UpdateProductRequest req) =>
        {
            var product = ProductStore.Update(id, req);
            return product is not null ? Results.Ok(product) : Results.NotFound();
        });

        app.MapDelete("/api/products/{id:int}", (int id) =>
        {
            var deleted = ProductStore.Delete(id);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }
}
