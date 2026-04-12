using Alba;
using Bobcat;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace BankAccountES.Tests;

[FixtureTitle("Bank Account ES")]
public class BankAccountFixture : Fixture
{
    private IAlbaHost _host = null!;

    private Client? _client;
    private Account? _account;
    private int _lastStatusCode;
    private AccountTransactions? _transactions;
    private List<Account>? _clientAccounts;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _client = null;
        _account = null;
        _lastStatusCode = 0;
        _transactions = null;
        _clientAccounts = null;
        return Task.CompletedTask;
    }

    // ── Given steps ──────────────────────────────────────────────────────────

    [Given("a client named {string} with email {string} is enrolled")]
    public async Task EnrollClient(string name, string email)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new EnrollClient(name, email)).ToUrl("/api/clients");
            x.StatusCodeShouldBe(200);
        });
        _client = result.ReadAsJson<Client>()!;
    }

    [Given("a {string} account is opened for the client")]
    public async Task OpenAccountGiven(string currency)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new OpenAccount(_client!.Id, currency)).ToUrl("/api/accounts");
            x.StatusCodeShouldBe(200);
        });
        _account = result.ReadAsJson<Account>()!;
    }

    [Given("{int} has been deposited into the account")]
    public async Task DepositGiven(int amount)
    {
        await _host.Scenario(x =>
        {
            x.Post.Json(new DepositFunds(_account!.Id, amount)).ToUrl($"/api/accounts/{_account.Id}/deposits");
            x.StatusCodeShouldBe(204);
        });
    }

    [Given("{int} has been withdrawn from the account")]
    public async Task WithdrawGiven(int amount)
    {
        await _host.Scenario(x =>
        {
            x.Post.Json(new WithdrawFunds(_account!.Id, amount)).ToUrl($"/api/accounts/{_account.Id}/withdrawals");
            x.StatusCodeShouldBe(204);
        });
    }

    // ── When steps ───────────────────────────────────────────────────────────

    [When("I enroll a client named {string} with email {string}")]
    public async Task EnrollClientWhen(string name, string email)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new EnrollClient(name, email)).ToUrl("/api/clients");
            x.StatusCodeShouldBe(200);
        });
        _client = result.ReadAsJson<Client>()!;
        _lastStatusCode = 200;
    }

    [When("I update the client name to {string} and email to {string}")]
    public async Task UpdateClient(string name, string email)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new UpdateClient(_client!.Id, name, email)).ToUrl($"/api/clients/{_client.Id}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I retrieve the client")]
    public async Task RetrieveClient()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/clients/{_client!.Id}");
            x.StatusCodeShouldBe(200);
        });
        _client = result.ReadAsJson<Client>()!;
    }

    [When("I open a {string} account for the client")]
    public async Task OpenAccountWhen(string currency)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new OpenAccount(_client!.Id, currency)).ToUrl("/api/accounts");
            x.StatusCodeShouldBe(200);
        });
        _account = result.ReadAsJson<Account>()!;
        _lastStatusCode = 200;
    }

    [When("I try to open an account for a non-existent client in {string}")]
    public async Task OpenAccountInvalidClient(string currency)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new OpenAccount(Guid.NewGuid(), currency)).ToUrl("/api/accounts");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I deposit {int} into the account")]
    public async Task DepositWhen(int amount)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new DepositFunds(_account!.Id, amount)).ToUrl($"/api/accounts/{_account.Id}/deposits");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;

        // Refresh account balance
        var getResult = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/accounts/{_account!.Id}");
            x.StatusCodeShouldBe(200);
        });
        _account = getResult.ReadAsJson<Account>()!;
    }

    [When("I withdraw {int} from the account")]
    public async Task WithdrawWhen(int amount)
    {
        await _host.Scenario(x =>
        {
            x.Post.Json(new WithdrawFunds(_account!.Id, amount)).ToUrl($"/api/accounts/{_account.Id}/withdrawals");
            x.StatusCodeShouldBe(204);
        });
        _lastStatusCode = 204;

        var getResult = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/accounts/{_account!.Id}");
            x.StatusCodeShouldBe(200);
        });
        _account = getResult.ReadAsJson<Account>()!;
    }

    [When("I try to withdraw {int} from the account")]
    public async Task TryWithdrawWhen(int amount)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new WithdrawFunds(_account!.Id, amount)).ToUrl($"/api/accounts/{_account.Id}/withdrawals");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I get the transaction history for the account")]
    public async Task GetTransactionHistory()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/accounts/{_account!.Id}/transactions");
            x.StatusCodeShouldBe(200);
        });
        _transactions = result.ReadAsJson<AccountTransactions>()!;
    }

    [When("I get the accounts for the client")]
    public async Task GetClientAccounts()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/clients/{_client!.Id}/accounts");
            x.StatusCodeShouldBe(200);
        });
        _clientAccounts = result.ReadAsJson<List<Account>>()!;
    }

    // ── Then / Check steps ───────────────────────────────────────────────────

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected)
    {
        _lastStatusCode.ShouldBe(expected);
    }

    [Check("the client id should be valid")]
    public bool ClientIdIsValid() => _client?.Id != Guid.Empty;

    [Then("the client name should be {string}")]
    public void ClientNameShouldBe(string expected) => _client!.Name.ShouldBe(expected);

    [Then("the client email should be {string}")]
    public void ClientEmailShouldBe(string expected) => _client!.Email.ShouldBe(expected);

    [Check("the account id should be valid")]
    public bool AccountIdIsValid() => _account?.Id != Guid.Empty;

    [Check("the account client id should match the client")]
    public bool AccountClientIdMatches() => _account?.ClientId == _client?.Id;

    [Then("the account currency should be {string}")]
    public void AccountCurrencyShouldBe(string expected) => _account!.Currency.ShouldBe(expected);

    [Then("the account balance should be {int}")]
    public void AccountBalanceShouldBe(int expected) => _account!.Balance.ShouldBe(expected);

    [Then("there should be {int} transactions")]
    public void TransactionCountShouldBe(int expected) => _transactions!.Transactions.Count.ShouldBe(expected);

    [Then("transaction {int} should be a {string} of {int}")]
    public void TransactionShouldBe(int index, string type, int amount)
    {
        var tx = _transactions!.Transactions[index - 1];
        tx.Type.ShouldBe(type);
        tx.Amount.ShouldBe(amount);
    }

    [Then("the transaction history balance should be {int}")]
    public void TransactionHistoryBalanceShouldBe(int expected) => _transactions!.Balance.ShouldBe(expected);

    [Then("there should be {int} client accounts")]
    public void ClientAccountCountShouldBe(int expected) => _clientAccounts!.Count.ShouldBe(expected);
}
