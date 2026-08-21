using Basket;
using Bobcat;
using Bobcat.Alba;
using Catalog;
using Discount;
using Ordering;
using Wolverine;
using Wolverine.Tracking;

namespace EcommerceModularMonolith.Tests;

/// <summary>
/// Specs for the modular-monolith eShop conversion. Reuses the host's own command and document
/// types via the project reference rather than re-declaring request/response records locally —
/// the file this replaced posted <c>CreateProductRequest(Name, Price)</c> to
/// <c>/catalog/products</c> when the host takes a five-field <c>CreateProduct</c> at
/// <c>/products</c>, stored baskets with a <c>(CustomerId, Items)</c> shape the host has never
/// had, checked out with a one-field request against a fourteen-field command, and created
/// "discounts with a percentage" for a Coupon that only has an Amount. Nothing had ever compiled
/// it, so nothing reported any of that.
/// </summary>
[FixtureTitle("Ecommerce Modular Monolith")]
public class EcommerceModularMonolithFixture : Fixture
{
    private Guid _productId;
    private string _productName = string.Empty;
    private decimal _productPrice;
    private Guid _customerId;
    private Guid _couponId;
    private string _couponProductName = string.Empty;
    private Guid _orderId;
    private int _lastStatusCode;
    private ShoppingCart? _basket;
    private Coupon? _coupon;
    private List<Product> _products = [];
    private List<OrderDto> _orders = [];

    public Task BeforeEach()
    {
        _productId = Guid.Empty;
        _productName = string.Empty;
        _productPrice = 0;
        // The checkout command wants a customer Guid, which the feature never names — the
        // basket is keyed by user name. One customer per scenario, minted here, is what ties
        // "the order created by the checkout" back to the checkout that created it.
        _customerId = Guid.NewGuid();
        _couponId = Guid.Empty;
        _couponProductName = string.Empty;
        _orderId = Guid.Empty;
        _lastStatusCode = 0;
        _basket = null;
        _coupon = null;
        _products = [];
        _orders = [];
        return Task.CompletedTask;
    }

    // ---- catalog ----------------------------------------------------------------------------

    [Given("I create a catalog product named {string} with price {decimal}")]
    public Task GivenCreateProduct(string name, decimal price) => createProductCore(name, price);

    [When("I create a catalog product named {string} with price {decimal}")]
    public Task WhenCreateProduct(string name, decimal price) => createProductCore(name, price);

    private async Task createProductCore(string name, decimal price)
    {
        // Category is required by the command's validator; the feature does not care which.
        var result = await Context!.PostJsonAsync<CreateProduct, Product>(
            "/products",
            new CreateProduct(name, ["Specs"], $"{name} description", $"{name}.png", price));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
        {
            _productId = result.Body.Id;
            _productName = result.Body.Name;
            _productPrice = result.Body.Price;
        }
    }

    [When("I get all catalog products")]
    public async Task GetAllProducts()
    {
        var result = await Context!.GetJsonAsync<List<Product>>("/products");
        _lastStatusCode = result.StatusCode;
        _products = result.Body ?? [];
    }

    [When("I get the catalog product by id")]
    public Task GetProductById() => getProductCore(_productId.ToString());

    [When("I get catalog product by id {string}")]
    public Task GetProductByStringId(string id) => getProductCore(id);

    private async Task getProductCore(string id)
    {
        var result = await Context!.GetJsonAsync<Product>($"/products/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I update the catalog product name to {string}")]
    public async Task UpdateProductName(string newName)
    {
        // PUT /products with the id in the body, not POST /products/{id} — that is the route the
        // host has, and [Entity] loads the Product from the command's Id. The other fields are
        // replayed from the create, because the command replaces the whole document.
        var result = await Context!.PutJsonAsync<UpdateProduct, Product>(
            "/products",
            new UpdateProduct(_productId, newName, ["Specs"], $"{newName} description", $"{newName}.png", _productPrice));

        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the catalog product")]
    public async Task DeleteProduct()
    {
        var result = await Context!.DeleteAsync($"/products/{_productId}");
        _lastStatusCode = result.StatusCode;
    }

    [Check("the catalog product id is returned")]
    public bool ProductIdReturned() => _productId != Guid.Empty;

    /// <summary>
    /// Reads the product back rather than trusting the write's response body, so an update
    /// that returned 200 without persisting would still fail here.
    /// </summary>
    [Check("the stored catalog product is named {string} with price {decimal}")]
    public async Task<bool> StoredProductIs(string name, decimal price)
    {
        var result = await Context!.GetJsonAsync<Product>($"/products/{_productId}");
        return result.Body is not null && result.Body.Name == name && result.Body.Price == price;
    }

    [Check("at least {int} catalog product is returned")]
    public bool AtLeastNProducts(int min) => _products.Count >= min;

    [Check("the catalog product is gone")]
    public async Task<bool> ProductIsGone()
    {
        var result = await Context!.GetJsonAsync<Product>($"/products/{_productId}");
        return result.StatusCode == 404;
    }

    // ---- basket -----------------------------------------------------------------------------

    [Given("I store a basket for customer {string} with the product")]
    public Task GivenStoreBasket(string userName) => storeBasketCore(userName);

    [When("I store a basket for customer {string} with the product")]
    public Task WhenStoreBasket(string userName) => storeBasketCore(userName);

    private async Task storeBasketCore(string userName)
    {
        // The basket is the host's own ShoppingCart document, keyed by user name, holding the
        // product the scenario just created at the price it was created with.
        var cart = new ShoppingCart
        {
            Id = userName,
            Items =
            [
                new ShoppingCartItem
                {
                    ProductId = _productId,
                    ProductName = _productName,
                    Quantity = 1,
                    Price = _productPrice,
                    Color = "Black",
                },
            ],
        };

        var result = await Context!.PostJsonAsync<StoreBasket, ShoppingCart>("/basket", new StoreBasket(cart));
        _lastStatusCode = result.StatusCode;
        _basket = result.Body;
    }

    [When("I get the basket for customer {string}")]
    public async Task GetBasket(string userName)
    {
        var result = await Context!.GetJsonAsync<ShoppingCart>($"/basket/{userName}");
        _lastStatusCode = result.StatusCode;
        _basket = result.Body;
    }

    [When("I delete the basket for customer {string}")]
    public async Task DeleteBasket(string userName)
    {
        var result = await Context!.DeleteAsync($"/basket/{userName}");
        _lastStatusCode = result.StatusCode;
    }

    [Given("I checkout the basket for customer {string}")]
    public Task GivenCheckout(string userName) => checkoutCore(userName);

    [When("I checkout the basket for customer {string}")]
    public Task WhenCheckout(string userName) => checkoutCore(userName);

    private async Task checkoutCore(string userName)
    {
        var command = new CheckoutBasket(
            userName,
            _customerId,
            "Jane", "Doe", "jane@example.com", "1 Main St", "US", "TX", "75001",
            "Jane Doe", "4111111111111111", "12/30", "123", PaymentMethod: 1);

        // Checkout returns 202 before the Ordering module has handled BasketCheckoutEvent, so
        // the call is tracked until the cascade is fully handled — see awaitingCascades.
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<CheckoutBasket, object>("/basket/checkout", command));

        _lastStatusCode = result.StatusCode;
    }

    [Check("the basket total is {decimal}")]
    public bool BasketTotalIs(decimal expected) => _basket?.TotalPrice == expected;

    [Check("the basket for customer {string} is gone")]
    public async Task<bool> BasketIsGone(string userName)
    {
        var result = await Context!.GetJsonAsync<ShoppingCart>($"/basket/{userName}");
        return result.StatusCode == 404;
    }

    // ---- ordering ---------------------------------------------------------------------------

    [When("I get all orders")]
    public async Task GetAllOrders()
    {
        var result = await Context!.GetJsonAsync<List<OrderDto>>("/orders");
        _lastStatusCode = result.StatusCode;
        _orders = result.Body ?? [];
    }

    [When("I get the order created by the checkout")]
    public async Task GetCheckoutOrder()
    {
        await locateCheckoutOrder();
        var result = await Context!.GetJsonAsync<OrderDto>($"/orders/{_orderId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the order created by the checkout")]
    public async Task DeleteCheckoutOrder()
    {
        await locateCheckoutOrder();
        var result = await Context!.DeleteAsync($"/orders/{_orderId}");
        _lastStatusCode = result.StatusCode;
    }

    /// <summary>
    /// Finds the order the Ordering module created for this scenario's customer. Looked up by
    /// customer rather than "the first order in the list" so the step cannot pick up an order
    /// another scenario, or another run, left behind.
    /// </summary>
    private async Task locateCheckoutOrder()
    {
        var result = await Context!.GetJsonAsync<List<OrderDto>>($"/orders/customer/{_customerId}");
        _orderId = result.Body?.FirstOrDefault()?.Id ?? Guid.Empty;
    }

    [Check("at least {int} order is returned")]
    public bool AtLeastNOrders(int min) => _orders.Count >= min;

    /// <summary>
    /// The sample's central claim: a checkout in the Basket module becomes an order in the
    /// Ordering module, carried over a durable local queue. The customer id is the thread that
    /// ties the two together.
    /// </summary>
    [Check("an order exists for the checked-out customer")]
    public bool OrderExistsForCustomer() => _orders.Any(o => o.CustomerId == _customerId);

    // ---- discount ---------------------------------------------------------------------------

    [Given("I create a discount for product {string} with amount {decimal}")]
    public Task GivenCreateDiscount(string productName, decimal amount) => createDiscountCore(productName, amount);

    [When("I create a discount for product {string} with amount {decimal}")]
    public Task WhenCreateDiscount(string productName, decimal amount) => createDiscountCore(productName, amount);

    private async Task createDiscountCore(string productName, decimal amount)
    {
        var result = await Context!.PostJsonAsync<CreateCoupon, Coupon>(
            "/discounts",
            new CreateCoupon(productName, $"{productName} discount", amount));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
        {
            _couponId = result.Body.Id;
            _couponProductName = result.Body.ProductName;
        }
    }

    [When("I get the discount for product {string}")]
    public async Task GetDiscount(string productName)
    {
        var result = await Context!.GetJsonAsync<Coupon>($"/discounts/{productName}");
        _lastStatusCode = result.StatusCode;
        _coupon = result.Body;
    }

    [When("I update the discount to amount {decimal}")]
    public async Task UpdateDiscount(decimal amount)
    {
        // PUT /discounts with the id in the body — [Entity] loads the Coupon from UpdateCoupon.Id.
        var result = await Context!.PutJsonAsync<UpdateCoupon, Coupon>(
            "/discounts",
            new UpdateCoupon(_couponId, _couponProductName, $"{_couponProductName} discount", amount));

        _lastStatusCode = result.StatusCode;
    }

    [When("I delete the discount")]
    public async Task DeleteDiscount()
    {
        var result = await Context!.DeleteAsync($"/discounts/{_couponId}");
        _lastStatusCode = result.StatusCode;
    }

    [Check("the discount amount is {decimal}")]
    public bool DiscountAmountIs(decimal expected) => _coupon?.Amount == expected;

    [Check("the stored discount for product {string} has amount {decimal}")]
    public async Task<bool> StoredDiscountIs(string productName, decimal amount)
    {
        var result = await Context!.GetJsonAsync<Coupon>($"/discounts/{productName}");
        return result.Body is not null && result.Body.Amount == amount;
    }

    // ---- shared -----------------------------------------------------------------------------

    [Check("the response status is {int}")]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    /// <summary>
    /// Run an HTTP call and wait for every message it cascades to be fully handled.
    ///
    /// Checkout publishes BasketCheckoutEvent to a <c>UseDurableInbox()</c> local queue, and the
    /// Ordering module creates the Order when it handles it — after the HTTP response has gone
    /// out. Asserting "an order exists" straight off the 202 would race the handler and fail
    /// intermittently. See docs/sample-wiring.md footgun 7; PaymentsMonolith is the worked
    /// example.
    /// </summary>
    private async Task<HttpResult<T>> awaitingCascades<T>(Func<Task<HttpResult<T>>> call)
    {
        var host = Context!.GetResource<IAlbaResource>().AlbaHost;
        HttpResult<T>? captured = null;

        // Explicitly typed: ExecuteAndWaitAsync overloads on Task and ValueTask, and an async
        // lambda is convertible to both.
        Func<IMessageContext, Task> act = async _ => { captured = await call(); };

        await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(act);

        return captured!;
    }
}
