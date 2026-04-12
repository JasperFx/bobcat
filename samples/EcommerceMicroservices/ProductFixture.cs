using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;

namespace EcommerceMicroservices;

[FixtureTitle("Product Catalog")]
public class ProductFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;
    private Product? _lastProduct;
    private int _currentProductId;
    private List<Product> _lastProductList = [];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    [When("I request the product list")]
    public async Task RequestProductList()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/products"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastProductList = JsonSerializer.Deserialize<List<Product>>(json, JsonOpts) ?? [];
    }

    [When("I create a product with name {string} price {int} stock {int} and category {string}")]
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

    [Given("a product exists with name {string} price {int} stock {int} and category {string}")]
    public async Task ProductExists(string name, int price, int stock, string category)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, price = (decimal)price, stock, category }).ToUrl("/api/products");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        var product = JsonSerializer.Deserialize<Product>(json, JsonOpts)!;
        _currentProductId = product.Id;
    }

    [When("I get the product by id")]
    public async Task GetProductById()
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/products/{_currentProductId}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastProduct = JsonSerializer.Deserialize<Product>(json, JsonOpts);
    }

    [When("I get a non-existent product with id {int}")]
    public async Task GetNonExistentProduct(int id)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/products/{id}");
            s.StatusCodeShouldBe(404);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I update the product price to {int}")]
    public async Task UpdateProductPrice(int price)
    {
        var product = ProductStore.GetById(_currentProductId)!;
        var result = await _host.Scenario(s =>
        {
            s.Put.Json(new { product.Name, price = (decimal)price, product.Stock, product.Category })
                .ToUrl($"/api/products/{_currentProductId}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastProduct = JsonSerializer.Deserialize<Product>(json, JsonOpts);
    }

    [When("I delete the product")]
    public async Task DeleteProduct()
    {
        var result = await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/products/{_currentProductId}");
            s.StatusCodeShouldBe(204);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I filter products by category {string}")]
    public async Task FilterProductsByCategory(string category)
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/products?category={category}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastProductList = JsonSerializer.Deserialize<List<Product>>(json, JsonOpts) ?? [];
    }

    [Then("the response is 200 OK")]
    public void ResponseIs200() => AssertStatus(200);

    [Then("the response is 201 Created")]
    public void ResponseIs201() => AssertStatus(201);

    [Then("the response is 204 No Content")]
    public void ResponseIs204() => AssertStatus(204);

    [Then("the response is 404 Not Found")]
    public void ResponseIs404() => AssertStatus(404);

    [Then("the product list is empty")]
    public void ProductListIsEmpty()
    {
        if (_lastProductList.Count != 0)
            throw new Exception($"Expected empty product list but got {_lastProductList.Count} products.");
    }

    [Then("the product list has {int} items")]
    public void ProductListHasCount(int expected)
    {
        if (_lastProductList.Count != expected)
            throw new Exception($"Expected {expected} products but got {_lastProductList.Count}.");
    }

    [Then("the product name is {string}")]
    public void ProductNameIs(string expected)
    {
        if (_lastProduct?.Name != expected)
            throw new Exception($"Expected product name '{expected}' but got '{_lastProduct?.Name}'.");
    }

    [Then("the product price is {int}")]
    public void ProductPriceIs(int expected)
    {
        if (_lastProduct?.Price != expected)
            throw new Exception($"Expected product price '{expected}' but got '{_lastProduct?.Price}'.");
    }

    [Then("the product stock is {int}")]
    public void ProductStockIs(int expected)
    {
        if (_lastProduct?.Stock != expected)
            throw new Exception($"Expected product stock '{expected}' but got '{_lastProduct?.Stock}'.");
    }

    [Then("the product category is {string}")]
    public void ProductCategoryIs(string expected)
    {
        if (_lastProduct?.Category != expected)
            throw new Exception($"Expected product category '{expected}' but got '{_lastProduct?.Category}'.");
    }

    [Then("all products in the list have category {string}")]
    public void AllProductsHaveCategory(string expected)
    {
        var wrong = _lastProductList.Where(p => !p.Category.Equals(expected, StringComparison.OrdinalIgnoreCase)).ToList();
        if (wrong.Any())
            throw new Exception($"Found products not in category '{expected}': {string.Join(", ", wrong.Select(p => p.Name))}");
    }

    private void AssertStatus(int expected)
    {
        if (_lastStatusCode != expected)
            throw new Exception($"Expected HTTP {expected} but got {_lastStatusCode}.");
    }
}
