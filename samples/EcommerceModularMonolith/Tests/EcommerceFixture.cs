using Alba;
using Basket;
using Bobcat;
using Bobcat.Runtime;
using Catalog;
using Discount;
using Ordering;
using Shouldly;

namespace EcommerceModularMonolith.Tests;

[FixtureTitle("Ecommerce Modular Monolith")]
public class EcommerceFixture : Fixture
{
    private IAlbaHost _host = null!;

    private Product? _product;
    private List<Product>? _products;
    private ShoppingCart? _basket;
    private Order? _order;
    private List<OrderDto>? _orders;
    private Coupon? _coupon;
    private int _lastStatusCode;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _product = null;
        _products = null;
        _basket = null;
        _order = null;
        _orders = null;
        _coupon = null;
        _lastStatusCode = 0;
        return Task.CompletedTask;
    }

    private async Task<Product> CreateProductInternal(string name, string category, decimal price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateProduct(name, [category], $"{name} desc", $"{name}.png", price)).ToUrl("/products");
            x.StatusCodeShouldBe(200);
        });
        return result.ReadAsJson<Product>()!;
    }

    private async Task<Order> CreateOrderInternal(string orderName)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateOrder(
                Guid.NewGuid(), orderName,
                "Jane", "Smith", "jane@test.com",
                "456 Oak Ave", "US", "NY", "10001",
                "Jane Smith", "4222222222222222", "06/29", "456", 1,
                [new OrderItem { ProductId = Guid.NewGuid(), ProductName = "Gadget", Quantity = 1, Price = 25m }]
            )).ToUrl("/orders");
            x.StatusCodeShouldBe(200);
        });
        return result.ReadAsJson<Order>()!;
    }

    // ── Given ────────────────────────────────────────────────────────────────

    [Given("a product named {string} in category {string} with price {int} exists")]
    public async Task CreateProductGiven(string name, string category, int price)
    {
        _product = await CreateProductInternal(name, category, price);
    }

    [Given("a basket for user {string} with product {string} quantity {int} and price {int} is stored")]
    public async Task StoreBasketGiven(string userId, string productName, int qty, int price)
    {
        var cart = new ShoppingCart
        {
            Id = userId,
            Items = [new ShoppingCartItem { ProductId = Guid.NewGuid(), ProductName = productName, Quantity = qty, Price = price, Color = "Red" }]
        };
        await _host.Scenario(x =>
        {
            x.Post.Json(new StoreBasket(cart)).ToUrl("/basket");
            x.StatusCodeShouldBe(200);
        });
    }

    [Given("an order named {string} exists")]
    public async Task CreateOrderGiven(string name)
    {
        _order = await CreateOrderInternal(name);
    }

    [Given("a coupon for product {string} with amount {int} exists")]
    public async Task CreateCouponGiven(string productName, int amount)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateCoupon(productName, "Discount", amount)).ToUrl("/discounts");
            x.StatusCodeShouldBe(200);
        });
        _coupon = result.ReadAsJson<Coupon>()!;
    }

    // ── When ─────────────────────────────────────────────────────────────────

    [When("I create a product named {string} in category {string} with price {decimal}")]
    public async Task CreateProduct(string name, string category, decimal price)
    {
        _product = await CreateProductInternal(name, category, price);
        _lastStatusCode = 200;
    }

    [When("I update the product to name {string} and price {int}")]
    public async Task UpdateProduct(string name, int price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateProduct(_product!.Id, name, ["NewCat"], $"{name} desc", "new.png", price)).ToUrl("/products");
            x.StatusCodeShouldBe(200);
        });
        _product = result.ReadAsJson<Product>()!;
        _lastStatusCode = 200;
    }

    [When("I delete the product")]
    public async Task DeleteProduct()
    {
        await _host.Scenario(x =>
        {
            x.Delete.Url($"/products/{_product!.Id}");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;
    }

    [When("I get all products")]
    public async Task GetAllProducts()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/products");
            x.StatusCodeShouldBe(200);
        });
        _products = result.ReadAsJson<List<Product>>()!;
    }

    [When("I get the product by id")]
    public async Task GetProductById()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/products/{_product!.Id}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        if (_lastStatusCode == 200)
            _product = result.ReadAsJson<Product>()!;
    }

    [When("I get products by category {string}")]
    public async Task GetProductsByCategory(string category)
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/products/category/{category}");
            x.StatusCodeShouldBe(200);
        });
        _products = result.ReadAsJson<List<Product>>()!;
    }

    [When("I store a basket for user {string} with product {string} quantity {int} and price {int}")]
    public async Task StoreBasket(string userId, string productName, int qty, int price)
    {
        var cart = new ShoppingCart
        {
            Id = userId,
            Items = [new ShoppingCartItem { ProductId = Guid.NewGuid(), ProductName = productName, Quantity = qty, Price = price, Color = "Red" }]
        };
        await _host.Scenario(x =>
        {
            x.Post.Json(new StoreBasket(cart)).ToUrl("/basket");
            x.StatusCodeShouldBe(200);
        });
        _lastStatusCode = 200;
    }

    [When("I get the basket for user {string}")]
    public async Task GetBasket(string userId)
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/basket/{userId}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        if (_lastStatusCode == 200)
            _basket = result.ReadAsJson<ShoppingCart>()!;
    }

    [When("I delete the basket for user {string}")]
    public async Task DeleteBasket(string userId)
    {
        await _host.Scenario(x =>
        {
            x.Delete.Url($"/basket/{userId}");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;
    }

    [When("I checkout the basket for user {string}")]
    public async Task CheckoutBasket(string userId)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CheckoutBasket(
                userId, Guid.NewGuid(),
                "John", "Doe", "john@test.com",
                "123 Main St", "US", "CA", "90210",
                "John Doe", "4111111111111111", "12/28", "123", 1
            )).ToUrl("/basket/checkout");
            x.StatusCodeShouldBe(200);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [Then("the basket for user {string} should be gone")]
    public async Task BasketShouldBeGone(string userId)
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/basket/{userId}");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I create an order named {string}")]
    public async Task CreateOrder(string name)
    {
        _order = await CreateOrderInternal(name);
        _lastStatusCode = 200;
    }

    [When("I get all orders")]
    public async Task GetAllOrders()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/orders");
            x.StatusCodeShouldBe(200);
        });
        _orders = result.ReadAsJson<List<OrderDto>>()!;
    }

    [When("I get the order by id")]
    public async Task GetOrderById()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/orders/{_order!.Id}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        if (_lastStatusCode == 200)
        {
            var dto = result.ReadAsJson<OrderDto>()!;
            _order = new Order { Id = dto.Id, OrderName = dto.OrderName, Status = dto.Status };
        }
    }

    [When("I delete the order")]
    public async Task DeleteOrder()
    {
        await _host.Scenario(x =>
        {
            x.Delete.Url($"/orders/{_order!.Id}");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;
    }

    [When("I create a coupon for product {string} with amount {int}")]
    public async Task CreateCoupon(string productName, int amount)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateCoupon(productName, "Discount", amount)).ToUrl("/discounts");
            x.StatusCodeShouldBe(200);
        });
        _coupon = result.ReadAsJson<Coupon>()!;
        _lastStatusCode = 200;
    }

    [When("I get the coupon for product {string}")]
    public async Task GetCouponByProduct(string productName)
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/discounts/{productName}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        if (_lastStatusCode == 200)
            _coupon = result.ReadAsJson<Coupon>()!;
    }

    [When("I update the coupon description to {string} and amount to {int}")]
    public async Task UpdateCoupon(string description, int amount)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateCoupon(_coupon!.Id, _coupon.ProductName, description, amount)).ToUrl("/discounts");
            x.StatusCodeShouldBe(200);
        });
        _coupon = result.ReadAsJson<Coupon>()!;
        _lastStatusCode = 200;
    }

    [When("I delete the coupon")]
    public async Task DeleteCoupon()
    {
        await _host.Scenario(x =>
        {
            x.Delete.Url($"/discounts/{_coupon!.Id}");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;
    }

    // ── Then / Check ─────────────────────────────────────────────────────────

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);

    [Check("the product id should be valid")]
    public bool ProductIdIsValid() => _product?.Id != Guid.Empty;

    [Then("the product name should be {string}")]
    public void ProductNameShouldBe(string expected) => _product!.Name.ShouldBe(expected);

    [Then("the product price should be {decimal}")]
    public void ProductPriceShouldBe(decimal expected) => _product!.Price.ShouldBe(expected);

    [Then("there should be at least {int} product")]
    public void ProductCountAtLeast(int min) => (_products!.Count >= min).ShouldBeTrue();

    [Then("the basket user id should be {string}")]
    public void BasketUserIdShouldBe(string expected) => _basket!.Id.ShouldBe(expected);

    [Then("the basket should have {int} item")]
    public void BasketItemCount(int expected) => _basket!.Items.Count.ShouldBe(expected);

    [Then("the order name should be {string}")]
    public void OrderNameShouldBe(string expected) => _order!.OrderName.ShouldBe(expected);

    [Then("the order status should be {string}")]
    public void OrderStatusShouldBe(string expected) =>
        _order!.Status.ToString().ShouldBe(expected);

    [Then("there should be at least {int} order")]
    public void OrderCountAtLeast(int min) => (_orders!.Count >= min).ShouldBeTrue();

    [Then("the coupon product name should be {string}")]
    public void CouponProductNameShouldBe(string expected) => _coupon!.ProductName.ShouldBe(expected);

    [Then("the coupon amount should be {int}")]
    public void CouponAmountShouldBe(int expected) => _coupon!.Amount.ShouldBe(expected);

    [Then("the coupon description should be {string}")]
    public void CouponDescriptionShouldBe(string expected) => _coupon!.Description.ShouldBe(expected);
}
