using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;

namespace BankAccountES;

[FixtureTitle("Bank Account Event Sourcing")]
public class AccountFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;
    private AccountView? _lastAccount;
    private DepositView? _lastDeposit;
    private string _currentAccountId = string.Empty;
    private List<TransactionView> _lastTransactions = [];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    [When("I open an account for {string} with initial deposit of {int}")]
    public async Task OpenAccount(string owner, int initialDeposit)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { owner, initialDeposit = (decimal)initialDeposit }).ToUrl("/api/accounts");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastAccount = JsonSerializer.Deserialize<AccountView>(json, JsonOpts);
        if (_lastAccount is not null) _currentAccountId = _lastAccount.Id;
    }

    [Given("an account exists for {string} with initial deposit of {int}")]
    public async Task AccountExists(string owner, int initialDeposit)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { owner, initialDeposit = (decimal)initialDeposit }).ToUrl("/api/accounts");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        var account = JsonSerializer.Deserialize<AccountView>(json, JsonOpts)!;
        _currentAccountId = account.Id;
    }

    [When("I get the account details")]
    public async Task GetAccountDetails()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/accounts/{_currentAccountId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastAccount = JsonSerializer.Deserialize<AccountView>(json, JsonOpts);
    }

    [When("I deposit {int} into the account")]
    public async Task DepositMoney(int amount)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { amount = (decimal)amount }).ToUrl($"/api/accounts/{_currentAccountId}/deposit");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastDeposit = JsonSerializer.Deserialize<DepositView>(json, JsonOpts);
        if (_lastDeposit is not null)
            _lastAccount = new AccountView(_lastDeposit.Id, _lastDeposit.Owner, _lastDeposit.Balance);
    }

    [When("I withdraw {int} from the account")]
    public async Task WithdrawMoney(int amount)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { amount = (decimal)amount }).ToUrl($"/api/accounts/{_currentAccountId}/withdraw");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        if (_lastStatusCode == 200)
            _lastAccount = JsonSerializer.Deserialize<AccountView>(json, JsonOpts);
    }

    [When("I attempt to withdraw {int} from the account")]
    public async Task AttemptWithdraw(int amount)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { amount = (decimal)amount }).ToUrl($"/api/accounts/{_currentAccountId}/withdraw");
            s.StatusCodeShouldBe(400);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I get the transaction history")]
    public async Task GetTransactionHistory()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/accounts/{_currentAccountId}/transactions"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastTransactions = JsonSerializer.Deserialize<List<TransactionView>>(json, JsonOpts) ?? [];
    }

    [Then("the response is 200 OK")]
    public void ResponseIs200() => AssertStatus(200);

    [Then("the response is 201 Created")]
    public void ResponseIs201() => AssertStatus(201);

    [Then("the response is 400 Bad Request")]
    public void ResponseIs400() => AssertStatus(400);

    [Then("the account owner is {string}")]
    public void AccountOwnerIs(string expected)
    {
        if (_lastAccount?.Owner != expected)
            throw new Exception($"Expected owner '{expected}' but got '{_lastAccount?.Owner}'.");
    }

    [Then("the account balance is {int}")]
    public void AccountBalanceIs(int expected)
    {
        if (_lastAccount?.Balance != (decimal)expected)
            throw new Exception($"Expected balance {expected} but got {_lastAccount?.Balance}.");
    }

    [Then("the transaction history has {int} entries")]
    public void TransactionHistoryHasEntries(int count)
    {
        if (_lastTransactions.Count != count)
            throw new Exception($"Expected {count} transactions but got {_lastTransactions.Count}.");
    }

    [Then("the transaction history contains a deposit of {int}")]
    public void TransactionHistoryContainsDeposit(int amount)
    {
        var found = _lastTransactions.Any(t => t.Type == "Deposit" && t.Amount == (decimal)amount);
        if (!found)
            throw new Exception($"Expected a deposit of {amount} in transaction history.");
    }

    private void AssertStatus(int expected)
    {
        if (_lastStatusCode != expected)
            throw new Exception($"Expected HTTP {expected} but got {_lastStatusCode}.");
    }
}
