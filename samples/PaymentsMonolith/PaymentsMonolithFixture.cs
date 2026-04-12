using Bobcat;
using Bobcat.Alba;

namespace PaymentsMonolith.Tests;

[FixtureTitle("Payments Monolith")]
public class PaymentsMonolithFixture
{
    private Guid _userId;
    private Guid _secondUserId;
    private int _lastStatusCode;
    private List<WalletDto> _wallets = [];

    [When("I register a user with email {string} and password {string}")]
    [Given("I register a user with email {string} and password {string}")]
    public async Task RegisterUser(IStepContext context, string email, string password)
    {
        var result = await context.PostJsonAsync<RegisterUserRequest, RegisterUserResponse>(
            "/api/users",
            new RegisterUserRequest(email, password));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null && _userId == Guid.Empty)
            _userId = result.Body.UserId;
    }

    [Given("I register a second user with email {string} and password {string}")]
    public async Task RegisterSecondUser(IStepContext context, string email, string password)
    {
        var result = await context.PostJsonAsync<RegisterUserRequest, RegisterUserResponse>(
            "/api/users",
            new RegisterUserRequest(email, password));
        if (result.Body is not null)
            _secondUserId = result.Body.UserId;
    }

    [When("I complete the customer profile with name {string}")]
    [Given("I complete the customer profile with name {string}")]
    public async Task CompleteCustomerProfile(IStepContext context, string name)
    {
        var result = await context.PostJsonAsync<CompleteCustomerRequest, object>(
            $"/api/users/{_userId}/complete",
            new CompleteCustomerRequest(_userId, name));
        _lastStatusCode = result.StatusCode;
    }

    [When("I add {float} to my wallet")]
    [Given("I add {float} to my wallet")]
    public async Task AddFunds(IStepContext context, float amount)
    {
        var result = await context.PostJsonAsync<AddFundsRequest, object>(
            $"/api/users/{_userId}/wallet/funds",
            new AddFundsRequest(_userId, (decimal)amount));
        _lastStatusCode = result.StatusCode;
    }

    [When("I transfer {float} to the second user")]
    public async Task TransferFunds(IStepContext context, float amount)
    {
        var result = await context.PostJsonAsync<TransferFundsRequest, object>(
            "/api/payments/transfers",
            new TransferFundsRequest(_userId, _secondUserId, (decimal)amount));
        _lastStatusCode = result.StatusCode;
    }

    [When("I get my wallets")]
    public async Task GetWallets(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<WalletDto>>($"/api/users/{_userId}/wallets");
        _lastStatusCode = result.StatusCode;
        _wallets = result.Body ?? [];
    }

    [When("I create a deposit for {float}")]
    public async Task CreateDeposit(IStepContext context, float amount)
    {
        var result = await context.PostJsonAsync<CreateDepositRequest, object>(
            "/api/payments/deposits",
            new CreateDepositRequest(_userId, (decimal)amount));
        _lastStatusCode = result.StatusCode;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Then("the user id is returned")]
    [Check]
    public bool UserIdReturned() => _userId != Guid.Empty;

    [Then("at least {int} wallet is returned")]
    [Check]
    public bool AtLeastNWallets(int min) => _wallets.Count >= min;
}

record RegisterUserRequest(string Email, string Password);
record RegisterUserResponse(Guid UserId);
record CompleteCustomerRequest(Guid UserId, string Name);
record AddFundsRequest(Guid UserId, decimal Amount);
record TransferFundsRequest(Guid SenderId, Guid ReceiverId, decimal Amount);
record WalletDto(Guid Id, decimal Balance, string Currency);
record CreateDepositRequest(Guid UserId, decimal Amount);
