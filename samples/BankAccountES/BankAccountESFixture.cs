using Bobcat;
using Bobcat.Alba;

namespace BankAccountES.Tests;

/// <summary>
/// Specs for the event-sourced bank account sample. Reuses the host's own command and aggregate
/// types via the project reference rather than re-declaring request/response records locally —
/// the file this replaced declared an <c>EnrollClientResponse(Guid ClientId)</c> against an
/// endpoint returning a <c>Client</c> whose id property is <c>Id</c>, posted to a PUT endpoint
/// with the wrong verb, and asserted a balance nothing ever assigned. Nothing had ever compiled
/// it, so nothing reported any of that.
/// </summary>
[FixtureTitle("Bank Account Event Sourcing")]
public class BankAccountESFixture : Fixture
{
    private Guid _clientId;
    private Guid _accountId;
    private string _email = string.Empty;
    private int _lastStatusCode;
    private AccountTransactions? _transactions;
    private List<Account> _accounts = [];

    public Task BeforeEach()
    {
        _clientId = Guid.Empty;
        _accountId = Guid.Empty;
        _email = string.Empty;
        _lastStatusCode = 0;
        _transactions = null;
        _accounts = [];
        return Task.CompletedTask;
    }

    // ---- enrollment -----------------------------------------------------------------------

    [Given("I enroll a client with name {string} and email {string}")]
    public Task GivenEnroll(string name, string email) => enrollCore(name, email);

    [When("I enroll a client with name {string} and email {string}")]
    public Task WhenEnroll(string name, string email) => enrollCore(name, email);

    private async Task enrollCore(string name, string email)
    {
        var result = await Context!.PostJsonAsync<EnrollClient, Client>(
            "/api/clients", new EnrollClient(name, email));

        _lastStatusCode = result.StatusCode;
        _email = email;
        if (result.Body is not null) _clientId = result.Body.Id;
    }

    [When("I update the client name to {string}")]
    public async Task UpdateClientName(string newName)
    {
        // PUT, not POST — the endpoint is a [WolverinePut], and the command carries the email
        // as well as the name, so the fixture replays the address it enrolled with.
        var result = await Context!.PutJsonAsync<UpdateClient, object>(
            $"/api/clients/{_clientId}", new UpdateClient(_clientId, newName, _email));

        _lastStatusCode = result.StatusCode;
    }

    // ---- accounts -------------------------------------------------------------------------

    [Given("I open a bank account for the client")]
    public Task GivenOpenAccount() => openAccountCore();

    [When("I open a bank account for the client")]
    public Task WhenOpenAccount() => openAccountCore();

    private async Task openAccountCore()
    {
        var result = await Context!.PostJsonAsync<OpenAccount, Account>(
            "/api/accounts", new OpenAccount(_clientId));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _accountId = result.Body.Id;
    }

    [When("I open a bank account for client id {string}")]
    public async Task OpenAccountForUnknownClient(string clientId)
    {
        var result = await Context!.PostJsonAsync<OpenAccount, object>(
            "/api/accounts", new OpenAccount(Guid.Parse(clientId)));

        _lastStatusCode = result.StatusCode;
    }

    // ---- deposits and withdrawals -----------------------------------------------------------

    [Given("I deposit {int} funds into the account")]
    public Task GivenDeposit(int amount) => depositCore(amount);

    [When("I deposit {int} funds into the account")]
    public Task WhenDeposit(int amount) => depositCore(amount);

    private async Task depositCore(int amount)
    {
        var result = await Context!.PostJsonAsync<DepositFunds, object>(
            $"/api/accounts/{_accountId}/deposits", new DepositFunds(_accountId, amount));

        _lastStatusCode = result.StatusCode;
    }

    [Given("I withdraw {int} funds from the account")]
    public Task GivenWithdraw(int amount) => withdrawCore(amount);

    [When("I withdraw {int} funds from the account")]
    public Task WhenWithdraw(int amount) => withdrawCore(amount);

    private async Task withdrawCore(int amount)
    {
        var result = await Context!.PostJsonAsync<WithdrawFunds, object>(
            $"/api/accounts/{_accountId}/withdrawals", new WithdrawFunds(_accountId, amount));

        _lastStatusCode = result.StatusCode;
    }

    // ---- queries --------------------------------------------------------------------------

    [When("I get the account transactions")]
    public async Task GetTransactions()
    {
        var result = await Context!.GetJsonAsync<AccountTransactions>(
            $"/api/accounts/{_accountId}/transactions");

        _lastStatusCode = result.StatusCode;
        _transactions = result.Body;
    }

    [When("I get the client accounts")]
    public async Task GetClientAccounts()
    {
        var result = await Context!.GetJsonAsync<List<Account>>(
            $"/api/clients/{_clientId}/accounts");

        _lastStatusCode = result.StatusCode;
        _accounts = result.Body ?? [];
    }

    // ---- assertions -------------------------------------------------------------------------

    [Check("the response status is {int}")]
    public bool StatusIs(int expectedStatus) => _lastStatusCode == expectedStatus;

    /// <summary>
    /// Reads the client back rather than trusting the write's response body — the point of the
    /// sample is that the aggregate is rebuilt from its events, so asserting against what the
    /// POST echoed would test nothing.
    /// </summary>
    [Check("the stored client is named {string} with email {string}")]
    public async Task<bool> StoredClientIs(string name, string email)
    {
        var result = await Context!.GetJsonAsync<Client>($"/api/clients/{_clientId}");
        return result.Body is not null && result.Body.Name == name && result.Body.Email == email;
    }

    [Check("the balance is {int}")]
    public async Task<bool> BalanceIs(int expected)
    {
        var result = await Context!.GetJsonAsync<Account>($"/api/accounts/{_accountId}");
        return result.Body is not null && result.Body.Balance == expected;
    }

    [Check("there are {int} transactions")]
    public bool TransactionCount(int expected) => _transactions?.Transactions.Count == expected;

    [Check("the transaction history balance is {int}")]
    public bool TransactionHistoryBalanceIs(int expected) => _transactions?.Balance == expected;

    [Check("there is {int} account")]
    public bool AccountCount(int expected) => _accounts.Count == expected;
}
