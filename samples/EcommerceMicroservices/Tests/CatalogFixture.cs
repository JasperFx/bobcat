using Alba;
using Bobcat;
using Bobcat.Runtime;
using Catalog;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EcommerceMicroservices.Tests;

[FixtureTitle("Ecommerce Microservices Catalog")]
public class CatalogFixture : Fixture
{
    private IAlbaHost _host = null!;

    private Product? _product;
    private List<Product>? _products;
    private int _lastStatusCode;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _product = null;
        _products = null;
        _lastStatusCode = 0;
        return Task.CompletedTask;
    }

    private async Task<Product> CreateProductInternal(string name, List<string> categories, string desc, decimal price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateProduct(name, categories, desc, $"{name}.png", price)).ToUrl("/products");
            x.StatusCodeShouldBeOk();
        });
        return result.ReadAsJson<Product>()!;
    }

    // ── Given ────────────────────────────────────────────────────────────────

    [Given("a product named {string} in category {string} with price {int} exists")]
    public async Task CreateProductGiven(string name, string category, int price)
    {
        _product = await CreateProductInternal(name, [category], $"{name} desc", price);
    }

    // ── When ─────────────────────────────────────────────────────────────────

    [When("I create a product named {string} in category {string} with price {decimal}")]
    public async Task CreateProduct(string name, string category, decimal price)
    {
        _product = await CreateProductInternal(name, [category], $"{name} desc", price);
        _lastStatusCode = 200;
    }

    [When("I try to create a product with empty name in category {string} with price {int}")]
    public async Task TryCreateEmptyName(string category, int price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateProduct("", [category], "desc", "img.png", price)).ToUrl("/products");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I try to create a product named {string} with empty category and price {int}")]
    public async Task TryCreateEmptyCategory(string name, int price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateProduct(name, [], "desc", "img.png", price)).ToUrl("/products");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I try to create a product named {string} in category {string} with price {int}")]
    public async Task TryCreateZeroPrice(string name, string category, int price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateProduct(name, [category], "desc", "img.png", price)).ToUrl("/products");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I get all products")]
    public async Task GetAllProducts()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/products");
            x.StatusCodeShouldBeOk();
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

    [When("I get a product by a random id")]
    public async Task GetProductByRandomId()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/products/{Guid.NewGuid()}");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I get products by category {string}")]
    public async Task GetProductsByCategory(string category)
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/products/category/{category}");
            x.StatusCodeShouldBeOk();
        });
        _products = result.ReadAsJson<List<Product>>()!;
    }

    [When("I update the product name to {string} category to {string} and price to {int}")]
    public async Task UpdateProduct(string name, string category, int price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateProduct(_product!.Id, name, [category], $"{name} desc", "updated.png", price)).ToUrl("/products");
            x.StatusCodeShouldBeOk();
        });
        _product = result.ReadAsJson<Product>()!;
        _lastStatusCode = 200;
    }

    [When("I try to update the product with empty name")]
    public async Task TryUpdateEmptyName()
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateProduct(_product!.Id, "", ["Cat"], "Desc", "img.png", 100m)).ToUrl("/products");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I try to update the product with price {int}")]
    public async Task TryUpdateZeroPrice(int price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateProduct(_product!.Id, "Name", ["Cat"], "Desc", "img.png", price)).ToUrl("/products");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I try to update a product with a random id")]
    public async Task TryUpdateRandomId()
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateProduct(Guid.NewGuid(), "Name", ["Cat"], "Desc", "img.png", 100m)).ToUrl("/products");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
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

    [When("I try to delete a product with a random id")]
    public async Task TryDeleteRandomId()
    {
        var result = await _host.Scenario(x =>
        {
            x.Delete.Url($"/products/{Guid.NewGuid()}");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    // ── Then / Check ─────────────────────────────────────────────────────────

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);

    [Check("the product id should be valid")]
    public bool ProductIdIsValid() => _product?.Id != Guid.Empty;

    [Then("the product name should be {string}")]
    public void ProductNameShouldBe(string expected) => _product!.Name.ShouldBe(expected);

    [Then("the product category should contain {string}")]
    public void ProductCategoryContains(string expected) => _product!.Category.ShouldContain(expected);

    [Then("the product price should be {decimal}")]
    public void ProductPriceShouldBe(decimal expected) => _product!.Price.ShouldBe(expected);

    [Then("there should be at least {int} products")]
    public void ProductCountAtLeast(int min) => (_products!.Count >= min).ShouldBeTrue();

    [Then("all products should be in category {string}")]
    public void AllProductsInCategory(string expected)
    {
        _products!.ShouldNotBeEmpty();
        foreach (var p in _products)
            p.Category.ShouldContain(expected);
    }
}
