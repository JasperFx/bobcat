using Alba;
using Bobcat;
using Bobcat.Runtime;
using Customers;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Payments;
using Shouldly;
using Users;
using Wallets;

namespace PaymentsMonolith.Tests;

[FixtureTitle("Payments Monolith")]
public class PaymentsFixture : Fixture
{
    private IAlbaHost _host = null!;

    private User? _user;
    private Customer? _customer;
    private Wallet? _wallet;
    private Wallet? _wallet1;
    private Wallet? _wallet2;
    private Deposit? _deposit;
    private List<Wallet>? _wallets;
    private int _lastStatusCode;
    private Guid _depositCustomerId;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _user = null;
        _customer = null;
        _wallet = null;
        _wallet1 = null;
        _wallet2 = null;
        _deposit = null;
        _wallets = null;
        _lastStatusCode = 0;
        _depositCustomerId = Guid.Empty;
        return Task.CompletedTask;
    }

    private async Task<(User user, Wallet wallet)> SetupUserWithWallet(string email, string name)
    {
        var user = (await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser(email, name, "password123")).ToUrl("/api/users");
            x.StatusCodeShouldBe(200);
        })).ReadAsJson<User>()!;

        await Task.Delay(1000);

        await _host.Scenario(x =>
        {
            x.Put.Json(new CompleteCustomer(user.Id, name.Split(' ')[0], name, "PL"))
                .ToUrl($"/api/customers/{user.Id}/complete");
            x.StatusCodeShouldBe(200);
        });

        await Task.Delay(1000);

        var wallets = (await _host.Scenario(x =>
        {
            x.Get.Url($"/api/wallets/owner/{user.Id}");
            x.StatusCodeShouldBe(200);
        })).ReadAsJson<List<Wallet>>()!;

        return (user, wallets[0]);
    }

    // ── Given ────────────────────────────────────────────────────────────────

    [Given("a user with email {string} is registered")]
    public async Task RegisterUserGiven(string email)
    {
        await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser(email, "First User", "password123")).ToUrl("/api/users");
            x.StatusCodeShouldBe(200);
        });
    }

    [Given("a user with email {string} name {string} is registered and the cascade handler has run")]
    public async Task RegisterUserAndWait(string email, string name)
    {
        _user = (await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser(email, name, "password123")).ToUrl("/api/users");
            x.StatusCodeShouldBe(200);
        })).ReadAsJson<User>()!;

        await Task.Delay(1000); // Wait for UserCreated handler to create the Customer
    }

    [Given("a user with email {string} name {string} has a wallet")]
    public async Task UserWithWallet(string email, string name)
    {
        var (user, wallet) = await SetupUserWithWallet(email, name);
        _user = user;
        _wallet = wallet;
    }

    [Given("two users each have a wallet")]
    public async Task TwoUsersWithWallets()
    {
        var (user1, w1) = await SetupUserWithWallet("xfer1@example.com", "Transfer User 1");
        var (user2, w2) = await SetupUserWithWallet("xfer2@example.com", "Transfer User 2");
        _wallet1 = w1;
        _wallet2 = w2;
    }

    [Given("{int} funds are added to the first wallet")]
    public async Task AddFundsToFirstWallet(int amount)
    {
        await _host.Scenario(x =>
        {
            x.Post.Json(new AddFunds(_wallet1!.Id, amount))
                .ToUrl($"/api/wallets/{_wallet1.Id}/funds/add");
            x.StatusCodeShouldBe(200);
        });
    }

    // ── When ─────────────────────────────────────────────────────────────────

    [When("I register a user with email {string} name {string} and password {string}")]
    public async Task RegisterUser(string email, string name, string password)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser(email, name, password)).ToUrl("/api/users");
            x.StatusCodeShouldBe(200);
        });
        _user = result.ReadAsJson<User>()!;
        _lastStatusCode = 200;
    }

    [When("I try to register a user with email {string} name {string} and password {string}")]
    public async Task TryRegisterUser(string email, string name, string password)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser(email, name, password)).ToUrl("/api/users");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I try to register another user with email {string}")]
    public async Task TryRegisterDuplicate(string email)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser(email, "Second User", "password456")).ToUrl("/api/users");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I complete the customer with nickname {string} and nationality {string}")]
    public async Task CompleteCustomer(string nickname, string nationality)
    {
        var result = await _host.Scenario(x =>
        {
            x.Put.Json(new CompleteCustomer(_user!.Id, nickname, _user.FullName, nationality))
                .ToUrl($"/api/customers/{_user.Id}/complete");
            x.StatusCodeShouldBe(200);
        });
        _customer = result.ReadAsJson<Customer>()!;
        _lastStatusCode = 200;
    }

    [When("I add {int} funds to the wallet")]
    public async Task AddFunds(int amount)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new AddFunds(_wallet!.Id, amount))
                .ToUrl($"/api/wallets/{_wallet.Id}/funds/add");
            x.StatusCodeShouldBe(200);
        });
        _wallet = result.ReadAsJson<Wallet>()!;
        _lastStatusCode = 200;
    }

    [When("I transfer {int} from the first wallet to the second wallet")]
    public async Task TransferFunds(int amount)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new TransferFunds(_wallet1!.Id, _wallet2!.Id, amount))
                .ToUrl("/api/wallets/transfer");
            x.StatusCodeShouldBe(200);
        });
        _wallet1 = result.ReadAsJson<Wallet>()!;

        var destResult = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/wallets/{_wallet2!.Id}");
            x.StatusCodeShouldBe(200);
        });
        _wallet2 = destResult.ReadAsJson<Wallet>()!;
        _lastStatusCode = 200;
    }

    [When("I try to transfer {int} from the first wallet which has 0 balance")]
    public async Task TryTransferInsufficientFunds(int amount)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new TransferFunds(_wallet1!.Id, _wallet2!.Id, amount))
                .ToUrl("/api/wallets/transfer");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I get all wallets")]
    public async Task GetAllWallets()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/api/wallets");
            x.StatusCodeShouldBe(200);
        });
        _wallets = result.ReadAsJson<List<Wallet>>()!;
        _lastStatusCode = 200;
    }

    [When("I create a deposit for customer {string} with currency {string} and amount {int}")]
    public async Task CreateDeposit(string customerId, string currency, int amount)
    {
        _depositCustomerId = Guid.Parse(customerId);
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateDeposit(_depositCustomerId, currency, amount))
                .ToUrl("/api/payments/deposits");
            x.StatusCodeShouldBe(200);
        });
        _deposit = result.ReadAsJson<Deposit>()!;
        _lastStatusCode = 200;
    }

    [When("I try to create a deposit with empty customer id and zero amount")]
    public async Task TryCreateInvalidDeposit()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateDeposit(Guid.Empty, "", 0m)).ToUrl("/api/payments/deposits");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    // ── Then / Check ─────────────────────────────────────────────────────────

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);

    [Check("the user id should be valid")]
    public bool UserIdIsValid() => _user?.Id != Guid.Empty;

    [Then("the user email should be {string}")]
    public void UserEmailShouldBe(string expected) => _user!.Email.ShouldBe(expected);

    [Then("the user full name should be {string}")]
    public void UserFullNameShouldBe(string expected) => _user!.FullName.ShouldBe(expected);

    [Check("the customer should be completed")]
    public bool CustomerIsCompleted() => _customer?.IsCompleted == true;

    [Then("the customer name should be {string}")]
    public void CustomerNameShouldBe(string expected) => _customer!.Name.ShouldBe(expected);

    [Then("the customer nationality should be {string}")]
    public void CustomerNationalityShouldBe(string expected) => _customer!.Nationality.ShouldBe(expected);

    [Then("the wallet balance should be {int}")]
    public void WalletBalanceShouldBe(int expected) => _wallet!.Balance.ShouldBe(expected);

    [Then("the first wallet balance should be {int}")]
    public void FirstWalletBalanceShouldBe(int expected) => _wallet1!.Balance.ShouldBe(expected);

    [Then("the second wallet balance should be {int}")]
    public void SecondWalletBalanceShouldBe(int expected) => _wallet2!.Balance.ShouldBe(expected);

    [Then("the wallets list should be returned")]
    public void WalletsListReturned() => _wallets.ShouldNotBeNull();

    [Check("the deposit customer id should match")]
    public bool DepositCustomerIdMatches() => _deposit?.CustomerId == _depositCustomerId;

    [Then("the deposit currency should be {string}")]
    public void DepositCurrencyShouldBe(string expected) => _deposit!.Currency.ShouldBe(expected);

    [Then("the deposit amount should be {int}")]
    public void DepositAmountShouldBe(int expected) => _deposit!.Amount.ShouldBe(expected);

    [Then("the deposit status should be {string}")]
    public void DepositStatusShouldBe(string expected) =>
        _deposit!.Status.ToString().ShouldBe(expected);
}
