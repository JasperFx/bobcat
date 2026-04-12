using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;

namespace PaymentsMonolith;

[FixtureTitle("Payments Monolith")]
public class PaymentsFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;

    private Account? _lastAccount;
    private Payment? _lastPayment;
    private List<Payment> _lastPaymentList = [];

    private int _currentAccountId;
    private int _secondAccountId;
    private int _currentPaymentId;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    // ---- Account steps ----

    [When("I create an account with name {string} email {string} balance {int}")]
    public async Task CreateAccount(string name, string email, int balance)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, email, initialBalance = (decimal)balance }).ToUrl("/api/accounts");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastAccount = JsonSerializer.Deserialize<Account>(json, JsonOpts);
        if (_lastAccount is not null) _currentAccountId = _lastAccount.Id;
    }

    [Given("an account exists with name {string} email {string} balance {int}")]
    public async Task AccountExists(string name, string email, int balance)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, email, initialBalance = (decimal)balance }).ToUrl("/api/accounts");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        _lastAccount = JsonSerializer.Deserialize<Account>(json, JsonOpts)!;
        _currentAccountId = _lastAccount.Id;
    }

    [Given("a second account exists with name {string} email {string} balance {int}")]
    public async Task SecondAccountExists(string name, string email, int balance)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { name, email, initialBalance = (decimal)balance }).ToUrl("/api/accounts");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        var account = JsonSerializer.Deserialize<Account>(json, JsonOpts)!;
        _secondAccountId = account.Id;
    }

    [When("I get the account by id")]
    public async Task GetAccountById()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/accounts/{_currentAccountId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastAccount = JsonSerializer.Deserialize<Account>(json, JsonOpts);
    }

    [When("I get the sender account")]
    public async Task GetSenderAccount()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/accounts/{_currentAccountId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastAccount = JsonSerializer.Deserialize<Account>(json, JsonOpts);
    }

    [When("I get the receiver account")]
    public async Task GetReceiverAccount()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/accounts/{_secondAccountId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastAccount = JsonSerializer.Deserialize<Account>(json, JsonOpts);
    }

    // ---- Payment steps ----

    [When("I make a payment of {int} from sender to receiver with reference {string}")]
    public async Task MakePayment(int amount, string reference)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new
            {
                fromAccountId = _currentAccountId,
                toAccountId = _secondAccountId,
                amount = (decimal)amount,
                currency = "USD",
                reference
            }).ToUrl("/api/payments");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastPayment = JsonSerializer.Deserialize<Payment>(json, JsonOpts);
        if (_lastPayment is not null) _currentPaymentId = _lastPayment.Id;
    }

    [When("I get the payment by id")]
    public async Task GetPaymentById()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/payments/{_currentPaymentId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastPayment = JsonSerializer.Deserialize<Payment>(json, JsonOpts);
    }

    [When("I get a non-existent payment")]
    public async Task GetNonExistentPayment()
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url("/api/payments/99999");
            s.StatusCodeShouldBe(404);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I view payment history for sender")]
    public async Task ViewPaymentHistoryForSender()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/accounts/{_currentAccountId}/payments"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastPaymentList = JsonSerializer.Deserialize<List<Payment>>(json, JsonOpts) ?? [];
    }

    [When("I refund the payment")]
    public async Task RefundPayment()
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Url($"/api/payments/{_currentPaymentId}/refund");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastPayment = JsonSerializer.Deserialize<Payment>(json, JsonOpts);
    }

    [When("I filter payments by status {string}")]
    public async Task FilterPaymentsByStatus(string status)
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/payments?status={status}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastPaymentList = JsonSerializer.Deserialize<List<Payment>>(json, JsonOpts) ?? [];
    }

    // ---- Assertion steps ----

    [Then("the response is 200 OK")]
    public void ResponseIs200() => AssertStatus(200);

    [Then("the response is 201 Created")]
    public void ResponseIs201() => AssertStatus(201);

    [Then("the response is 404 Not Found")]
    public void ResponseIs404() => AssertStatus(404);

    [Then("the account name is {string}")]
    public void AccountNameIs(string expected)
    {
        if (_lastAccount?.Name != expected)
            throw new Exception($"Expected account name '{expected}' but got '{_lastAccount?.Name}'.");
    }

    [Then("the account balance is {int}")]
    public void AccountBalanceIs(int expected)
    {
        if (_lastAccount?.Balance != (decimal)expected)
            throw new Exception($"Expected balance {expected} but got {_lastAccount?.Balance}.");
    }

    [Then("the payment status is {string}")]
    public void PaymentStatusIs(string expected)
    {
        if (_lastPayment?.Status != expected)
            throw new Exception($"Expected payment status '{expected}' but got '{_lastPayment?.Status}'.");
    }

    [Then("the payment amount is {int}")]
    public void PaymentAmountIs(int expected)
    {
        if (_lastPayment?.Amount != (decimal)expected)
            throw new Exception($"Expected payment amount {expected} but got {_lastPayment?.Amount}.");
    }

    [Then("the payment reference is {string}")]
    public void PaymentReferenceIs(string expected)
    {
        if (_lastPayment?.Reference != expected)
            throw new Exception($"Expected payment reference '{expected}' but got '{_lastPayment?.Reference}'.");
    }

    [Then("the payment history has {int} entries")]
    public void PaymentHistoryHasCount(int expected)
    {
        if (_lastPaymentList.Count != expected)
            throw new Exception($"Expected {expected} payment history entries but got {_lastPaymentList.Count}.");
    }

    [Then("the filtered payment list has {int} entries")]
    public void FilteredPaymentListHasCount(int expected)
    {
        if (_lastPaymentList.Count != expected)
            throw new Exception($"Expected {expected} filtered payments but got {_lastPaymentList.Count}.");
    }

    private void AssertStatus(int expected)
    {
        if (_lastStatusCode != expected)
            throw new Exception($"Expected HTTP {expected} but got {_lastStatusCode}.");
    }
}
