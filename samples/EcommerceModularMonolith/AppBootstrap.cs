namespace EcommerceModularMonolith;

public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        // Products
        app.MapPost("/api/products", (CreateProductRequest req) =>
        {
            var product = Store.CreateProduct(req);
            return Results.Created($"/api/products/{product.Id}", product);
        });

        app.MapGet("/api/products", () => Store.GetAllProducts());

        app.MapGet("/api/products/{id:int}", (int id) =>
        {
            var product = Store.GetProduct(id);
            return product is not null ? Results.Ok(product) : Results.NotFound();
        });

        app.MapPut("/api/products/{id:int}/stock", (int id, UpdateStockRequest req) =>
        {
            var product = Store.UpdateStock(id, req.Adjustment);
            return product is not null ? Results.Ok(product) : Results.NotFound();
        });

        // Customers
        app.MapPost("/api/customers", (CreateCustomerRequest req) =>
        {
            var customer = Store.CreateCustomer(req);
            return Results.Created($"/api/customers/{customer.Id}", customer);
        });

        app.MapGet("/api/customers", () => Store.GetAllCustomers());

        app.MapGet("/api/customers/{id:int}", (int id) =>
        {
            var customer = Store.GetCustomer(id);
            return customer is not null ? Results.Ok(customer) : Results.NotFound();
        });

        app.MapGet("/api/customers/{id:int}/orders", (int id) =>
        {
            return Store.GetOrdersForCustomer(id);
        });

        // Orders
        app.MapPost("/api/orders", (PlaceOrderRequest req) =>
        {
            var (order, error) = Store.PlaceOrder(req);
            if (order is null) return Results.BadRequest(new { error });
            return Results.Created($"/api/orders/{order.Id}", order);
        });

        app.MapGet("/api/orders", () => Store.GetAllOrders());

        app.MapGet("/api/orders/{id:int}", (int id) =>
        {
            var order = Store.GetOrder(id);
            return order is not null ? Results.Ok(order) : Results.NotFound();
        });

        app.MapPost("/api/orders/{id:int}/confirm", (int id) =>
        {
            var order = Store.UpdateOrderStatus(id, "confirmed");
            return order is not null ? Results.Ok(order) : Results.NotFound();
        });

        app.MapPost("/api/orders/{id:int}/ship", (int id) =>
        {
            var order = Store.UpdateOrderStatus(id, "shipped");
            return order is not null ? Results.Ok(order) : Results.NotFound();
        });

        app.MapPost("/api/orders/{id:int}/cancel", (int id) =>
        {
            var order = Store.UpdateOrderStatus(id, "cancelled");
            return order is not null ? Results.Ok(order) : Results.NotFound();
        });
    }
}
