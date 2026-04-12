using Bobcat;
using Bobcat.Alba;

namespace BankAccountES.Tests;

[FixtureTitle("Bank Account Event Sourcing")]
public class BankAccountESFixture
{
    private Guid _clientId;
    private Guid _accountId;
    private int _lastStatusCode;

    [SetUp]
    public async Task SetUp(IStepContext context)
    {
        // Reset state per scenario
        _clientId = Guid.Empty;
        _accountId = Guid.Empty;
    }

    [Given("I enroll a client with name {string} and email {string}")]
    public async Task EnrollClient(IStepContext context, string name, string email)
    {
        var result = await context.PostJsonAsync<EnrollClientRequest, EnrollClientResponse>(
            "/api/clients",
            new EnrollClientRequest(name, email));
        _lastStatusCode = result.StatusCode;
        _clientId = result.Body!.ClientId;
    }

    [When("I update the client name to {string}")]
    public async Task UpdateClient(IStepContext context, string newName)
    {
        var result = await context.PostJsonAsync<UpdateClientRequest, object>(
            $"/api/clients/{_clientId}",
            new UpdateClientRequest(_clientId, newName));
        _lastStatusCode = result.StatusCode;
    }

    [When("I open a bank account for the client")]
    [Given("I open a bank account for the client")]
    public async Task OpenAccount(IStepContext context)
    {
        var result = await context.PostJsonAsync<OpenAccountRequest, OpenAccountResponse>(
            "/api/accounts",
            new OpenAccountRequest(_clientId));
        _lastStatusCode = result.StatusCode;
        _accountId = result.Body!.AccountId;
    }

    [When("I open a bank account for client id {string}")]
    public async Task OpenAccountForInvalidClient(IStepContext context, string clientId)
    {
        var result = await context.PostJsonAsync<OpenAccountRequest, object>(
            "/api/accounts",
            new OpenAccountRequest(Guid.Parse(clientId)));
        _lastStatusCode = result.StatusCode;
    }

    [When("I deposit {int} funds into the account")]
    [Given("I deposit {int} funds into the account")]
    public async Task DepositFunds(IStepContext context, int amount)
    {
        var result = await context.PostJsonAsync<DepositFundsRequest, object>(
            $"/api/accounts/{_accountId}/deposits",
            new DepositFundsRequest(amount));
        _lastStatusCode = result.StatusCode;
    }

    [When("I withdraw {int} funds from the account")]
    [Given("I withdraw {int} funds from the account")]
    public async Task WithdrawFunds(IStepContext context, int amount)
    {
        var result = await context.PostJsonAsync<WithdrawFundsRequest, object>(
            $"/api/accounts/{_accountId}/withdrawals",
            new WithdrawFundsRequest(amount));
        _lastStatusCode = result.StatusCode;
    }

    [When("I get the account transactions")]
    public async Task GetTransactions(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<TransactionResponse>>(
            $"/api/accounts/{_accountId}/transactions");
        _lastStatusCode = result.StatusCode;
        _transactions = result.Body!;
    }

    private List<TransactionResponse> _transactions = [];
    private List<AccountResponse> _accounts = [];
    private decimal _balance;

    [When("I get the client accounts")]
    public async Task GetClientAccounts(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<AccountResponse>>(
            $"/api/clients/{_clientId}/accounts");
        _lastStatusCode = result.StatusCode;
        _accounts = result.Body!;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expectedStatus) => _lastStatusCode == expectedStatus;

    [Then("the client is stored")]
    [Check]
    public bool ClientIsStored() => _clientId != Guid.Empty;

    [Then("the account is active")]
    [Check]
    public bool AccountIsActive() => _accountId != Guid.Empty;

    [Then("the balance is {int}")]
    [Check]
    public bool BalanceIs(int expected) => _balance == expected;

    [Then("there are {int} transactions")]
    [Check]
    public bool TransactionCount(int expected) => _transactions.Count == expected;

    [Then("there is {int} account")]
    [Check]
    public bool AccountCount(int expected) => _accounts.Count == expected;
}

record EnrollClientRequest(string Name, string Email);
record EnrollClientResponse(Guid ClientId);
record UpdateClientRequest(Guid ClientId, string Name);
record OpenAccountRequest(Guid ClientId);
record OpenAccountResponse(Guid AccountId);
record DepositFundsRequest(decimal Amount);
record WithdrawFundsRequest(decimal Amount);
record TransactionResponse(Guid Id, decimal Amount, string Type);
record AccountResponse(Guid AccountId, decimal Balance);
