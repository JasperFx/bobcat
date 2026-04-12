using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;

namespace EcommerceModularMonolith;

[FixtureTitle("Ecommerce Modular Monolith")]
public class EcommerceFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;

    private Product? _lastProduct;
    private Customer? _lastCustomer;
    private Order? _lastOrder;
    private List<Product> _lastProductList = [];
    private List<Customer> _lastCustomerList = [];
    private List<Order> _lastOrderList = [];

    private int _currentProductId;
    private int _currentCustomerId;
    private int _currentOrderId;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    // ---- Product steps ----

    [When("I create a product with name {string} price {int} stock {int} category {string}")]
    public async Task CreateProduct(string name, int price, int stock, string category)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, price = (decimal)price, stock, category }).ToUrl("/api/products");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastProduct = JsonSerializer.Deserialize<Product>(json, JsonOpts);
        if (_lastProduct is not null) _currentProductId = _lastProduct.Id;
    }

    [Given("a product exists with name {string} price {int} stock {int} category {string}")]
    public async Task ProductExists(string name, int price, int stock, string category)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, price = (decimal)price, stock, category }).ToUrl("/api/products");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        _lastProduct = JsonSerializer.Deserialize<Product>(json, JsonOpts)!;
        _currentProductId = _lastProduct.Id;
    }

    [When("I list all products")]
    public async Task ListProducts()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/products"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastProductList = JsonSerializer.Deserialize<List<Product>>(json, JsonOpts) ?? [];
    }

    [When("I get the product by id")]
    public async Task GetProductById()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/products/{_currentProductId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastProduct = JsonSerializer.Deserialize<Product>(json, JsonOpts);
    }

    [When("I update product stock by {int}")]
    public async Task UpdateProductStock(int adjustment)
    {
        var result = await _host.Scenario(s =>
        {
            s.Put.Json(new { adjustment }).ToUrl($"/api/products/{_currentProductId}/stock");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastProduct = JsonSerializer.Deserialize<Product>(json, JsonOpts);
    }

    // ---- Customer steps ----

    [When("I create a customer with name {string} email {string} address {string}")]
    public async Task CreateCustomer(string name, string email, string address)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, email, address }).ToUrl("/api/customers");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastCustomer = JsonSerializer.Deserialize<Customer>(json, JsonOpts);
        if (_lastCustomer is not null) _currentCustomerId = _lastCustomer.Id;
    }

    [Given("a customer exists with name {string} email {string} address {string}")]
    public async Task CustomerExists(string name, string email, string address)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, email, address }).ToUrl("/api/customers");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        _lastCustomer = JsonSerializer.Deserialize<Customer>(json, JsonOpts)!;
        _currentCustomerId = _lastCustomer.Id;
    }

    [When("I list all customers")]
    public async Task ListCustomers()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/customers"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastCustomerList = JsonSerializer.Deserialize<List<Customer>>(json, JsonOpts) ?? [];
    }

    [When("I get the customer by id")]
    public async Task GetCustomerById()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/customers/{_currentCustomerId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastCustomer = JsonSerializer.Deserialize<Customer>(json, JsonOpts);
    }

    [When("I list orders for the customer")]
    public async Task ListOrdersForCustomer()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/customers/{_currentCustomerId}/orders"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastOrderList = JsonSerializer.Deserialize<List<Order>>(json, JsonOpts) ?? [];
    }

    // ---- Order steps ----

    [When("I place an order for product {int} quantity {int}")]
    public async Task PlaceOrderSingleItem(int productId, int qty)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new
            {
                customerId = _currentCustomerId,
                items = new[] { new { productId, qty } }
            }).ToUrl("/api/orders");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastOrder = JsonSerializer.Deserialize<Order>(json, JsonOpts);
        if (_lastOrder is not null) _currentOrderId = _lastOrder.Id;
    }

    [When("I get the order by id")]
    public async Task GetOrderById()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/orders/{_currentOrderId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastOrder = JsonSerializer.Deserialize<Order>(json, JsonOpts);
    }

    [When("I get a non-existent order")]
    public async Task GetNonExistentOrder()
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url("/api/orders/99999");
            s.StatusCodeShouldBe(404);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I list all orders")]
    public async Task ListAllOrders()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/orders"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastOrderList = JsonSerializer.Deserialize<List<Order>>(json, JsonOpts) ?? [];
    }

    [When("I confirm the order")]
    public async Task ConfirmOrder()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/orders/{_currentOrderId}/confirm");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastOrder = JsonSerializer.Deserialize<Order>(json, JsonOpts);
    }

    [When("I ship the order")]
    public async Task ShipOrder()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/orders/{_currentOrderId}/ship");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastOrder = JsonSerializer.Deserialize<Order>(json, JsonOpts);
    }

    [When("I cancel the order")]
    public async Task CancelOrder()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/orders/{_currentOrderId}/cancel");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastOrder = JsonSerializer.Deserialize<Order>(json, JsonOpts);
    }

    // ---- Assertion steps ----

    [Then("the response is 200 OK")]
    public void ResponseIs200() => AssertStatus(200);

    [Then("the response is 201 Created")]
    public void ResponseIs201() => AssertStatus(201);

    [Then("the response is 404 Not Found")]
    public void ResponseIs404() => AssertStatus(404);

    [Then("the product list has {int} items")]
    public void ProductListHasCount(int expected)
    {
        if (_lastProductList.Count != expected)
            throw new Exception($"Expected {expected} products but got {_lastProductList.Count}.");
    }

    [Then("the customer list has {int} items")]
    public void CustomerListHasCount(int expected)
    {
        if (_lastCustomerList.Count != expected)
            throw new Exception($"Expected {expected} customers but got {_lastCustomerList.Count}.");
    }

    [Then("the order list has {int} items")]
    public void OrderListHasCount(int expected)
    {
        if (_lastOrderList.Count != expected)
            throw new Exception($"Expected {expected} orders but got {_lastOrderList.Count}.");
    }

    [Then("the product name is {string}")]
    public void ProductNameIs(string expected)
    {
        if (_lastProduct?.Name != expected)
            throw new Exception($"Expected product name '{expected}' but got '{_lastProduct?.Name}'.");
    }

    [Then("the product stock is {int}")]
    public void ProductStockIs(int expected)
    {
        if (_lastProduct?.Stock != expected)
            throw new Exception($"Expected stock {expected} but got {_lastProduct?.Stock}.");
    }

    [Then("the customer name is {string}")]
    public void CustomerNameIs(string expected)
    {
        if (_lastCustomer?.Name != expected)
            throw new Exception($"Expected customer name '{expected}' but got '{_lastCustomer?.Name}'.");
    }

    [Then("the order status is {string}")]
    public void OrderStatusIs(string expected)
    {
        if (_lastOrder?.Status != expected)
            throw new Exception($"Expected order status '{expected}' but got '{_lastOrder?.Status}'.");
    }

    [Then("the order total is {int}")]
    public void OrderTotalIs(int expected)
    {
        if (_lastOrder?.Total != (decimal)expected)
            throw new Exception($"Expected order total {expected} but got {_lastOrder?.Total}.");
    }

    [Then("the order has {int} items")]
    public void OrderHasItems(int expected)
    {
        var actual = _lastOrder?.Items.Count ?? 0;
        if (actual != expected)
            throw new Exception($"Expected {expected} order items but got {actual}.");
    }

    [Then("the order item product is {string}")]
    public void OrderItemProductIs(string expectedName)
    {
        var item = _lastOrder?.Items.FirstOrDefault();
        if (item?.ProductName != expectedName)
            throw new Exception($"Expected order item product '{expectedName}' but got '{item?.ProductName}'.");
    }

    [Then("the customer order list has {int} items")]
    public void CustomerOrderListHasCount(int expected)
    {
        if (_lastOrderList.Count != expected)
            throw new Exception($"Expected {expected} orders for customer but got {_lastOrderList.Count}.");
    }

    private void AssertStatus(int expected)
    {
        if (_lastStatusCode != expected)
            throw new Exception($"Expected HTTP {expected} but got {_lastStatusCode}.");
    }
}
