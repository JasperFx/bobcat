using Bobcat;
using Bobcat.Alba;

namespace EcommerceMicroservices.Tests;

[FixtureTitle("Ecommerce Microservices Products")]
public class EcommerceMicroservicesFixture
{
    private Guid _productId;
    private int _lastStatusCode;
    private ProductDto? _product;
    private List<ProductDto> _products = [];

    [When("I create a product named {string} in category {string} with price {float}")]
    [Given("I create a product named {string} in category {string} with price {float}")]
    public async Task CreateProduct(IStepContext context, string name, string category, float price)
    {
        var result = await context.PostJsonAsync<CreateProductRequest, CreateProductResponse>(
            "/products",
            new CreateProductRequest(name, category, (decimal)price));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _productId = result.Body.Id;
    }

    [When("I create a product with empty name")]
    public async Task CreateProductInvalid(IStepContext context)
    {
        var result = await context.PostJsonAsync<CreateProductRequest, object>(
            "/products",
            new CreateProductRequest("", "Category", 1.0m));
        _lastStatusCode = result.StatusCode;
    }

    [When("I get all products")]
    public async Task GetAllProducts(IStepContext context)
    {
        var result = await context.GetJsonAsync<GetProductsResponse>("/products");
        _lastStatusCode = result.StatusCode;
        _products = result.Body?.Products ?? [];
    }

    [When("I get the product by id")]
    public async Task GetProductById(IStepContext context)
    {
        var result = await context.GetJsonAsync<GetProductResponse>($"/products/{_productId}");
        _lastStatusCode = result.StatusCode;
        _product = result.Body?.Product;
    }

    [When("I get product by id {string}")]
    public async Task GetProductByStringId(IStepContext context, string id)
    {
        var result = await context.GetJsonAsync<GetProductResponse>($"/products/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I get products in category {string}")]
    public async Task GetProductsByCategory(IStepContext context, string category)
    {
        var result = await context.GetJsonAsync<GetProductsResponse>($"/products/category/{category}");
        _lastStatusCode = result.StatusCode;
        _products = result.Body?.Products ?? [];
    }

    [When("I update the product name to {string} with price {float}")]
    public async Task UpdateProduct(IStepContext context, string name, float price)
    {
        var result = await context.PostJsonAsync<UpdateProductRequest, object>(
            $"/products/{_productId}",
            new UpdateProductRequest(_productId, name, (decimal)price));
        _lastStatusCode = result.StatusCode;
    }

    [When("I update the product with empty name")]
    public async Task UpdateProductInvalid(IStepContext context)
    {
        var result = await context.PostJsonAsync<UpdateProductRequest, object>(
            $"/products/{_productId}",
            new UpdateProductRequest(_productId, "", 1.0m));
        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the product")]
    [Given("I delete the product")]
    public async Task DeleteProduct(IStepContext context)
    {
        var result = await context.DeleteAsync($"/products/{_productId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I delete product by id {string}")]
    public async Task DeleteProductByStringId(IStepContext context, string id)
    {
        var result = await context.DeleteAsync($"/products/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Then("the product id is returned")]
    [Check]
    public bool ProductIdReturned() => _productId != Guid.Empty;

    [Then("at least {int} product is returned")]
    [Check]
    public bool AtLeastNProducts(int min) => _products.Count >= min;

    [Then("the product name is {string}")]
    [Check]
    public bool ProductNameIs(string expected) => _product?.Name == expected;
}

record CreateProductRequest(string Name, string Category, decimal Price);
record CreateProductResponse(Guid Id);
record UpdateProductRequest(Guid Id, string Name, decimal Price);
record ProductDto(Guid Id, string Name, string Category, decimal Price);
record GetProductsResponse(List<ProductDto> Products);
record GetProductResponse(ProductDto Product);
