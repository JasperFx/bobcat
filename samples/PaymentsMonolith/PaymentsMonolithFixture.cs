using Bobcat;
using Bobcat.Alba;
using Customers;
using Payments;
using Users;
using Wallets;
using Wolverine;
using Wolverine.Tracking;

namespace PaymentsMonolith.Tests;

/// <summary>
/// Specs for the Inflow conversion. Reuses the host's own command and document types via the
/// project reference rather than re-declaring request records locally — the file this replaced
/// declared its own <c>RegisterUserRequest(Email, Password)</c> against an endpoint that takes
/// <c>RegisterUser(Email, FullName, Password)</c>, and nothing ever compiled it, so nothing
/// reported the drift.
/// </summary>
[FixtureTitle("Payments Monolith")]
public class PaymentsMonolithFixture : Fixture
{
    private const string Password = "Password1!";

    private Guid _customerId;
    private Guid _walletId;
    private Guid _secondCustomerId;
    private Guid _secondWalletId;
    private int _lastStatusCode;
    private Wallet? _wallet;
    private Deposit? _deposit;
    private Customer? _customer;

    public Task BeforeEach()
    {
        _customerId = Guid.Empty;
        _walletId = Guid.Empty;
        _secondCustomerId = Guid.Empty;
        _secondWalletId = Guid.Empty;
        _lastStatusCode = 0;
        _wallet = null;
        _deposit = null;
        _customer = null;
        return Task.CompletedTask;
    }

    // ---- registration -----------------------------------------------------------------------

    [Given("I register {string} as {string}")]
    public Task GivenRegister(string email, string fullName) => registerCore(email, fullName);

    [When("I register {string} as {string}")]
    public Task WhenRegister(string email, string fullName) => registerCore(email, fullName);

    private async Task registerCore(string email, string fullName)
    {
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<RegisterUser, User>(
                "/api/users",
                new RegisterUser(email, fullName, Password)));

        _lastStatusCode = result.StatusCode;

        // The Customer is created by the Customers module in response to the cascaded
        // UserCreated, and carries the same id as the User.
        if (result.Body is not null && result.Body.Id != Guid.Empty && _customerId == Guid.Empty)
            _customerId = result.Body.Id;
    }

    // ---- customer profile -------------------------------------------------------------------

    [Given("I complete the customer profile as {string} from {string}")]
    public Task GivenCompleteProfile(string name, string nationality)
        => completeProfileCore(_customerId, name, nationality);

    [When("I complete the customer profile as {string} from {string}")]
    public Task WhenCompleteProfile(string name, string nationality)
        => completeProfileCore(_customerId, name, nationality);

    [When("I complete the profile of a customer that does not exist")]
    public Task CompleteUnknownProfile()
        => completeProfileCore(Guid.NewGuid(), "Nobody", "Nowhere");

    private async Task completeProfileCore(Guid customerId, string name, string nationality)
    {
        var result = await awaitingCascades(() =>
            Context!.PutJsonAsync<CompleteCustomer, Customer>(
                $"/api/customers/{customerId}/complete",
                new CompleteCustomer(customerId, name, name, nationality)));

        _lastStatusCode = result.StatusCode;
        _customer = result.Body;

        // Completing the profile is what makes the Wallets module create a wallet, so this is
        // the first point at which the owner has one to look up.
        if (result.StatusCode == 200)
            (_walletId, _wallet) = await loadWallet(customerId);
    }

    [Given("a second customer {string} named {string}")]
    public async Task GivenSecondCustomer(string email, string fullName)
    {
        var registered = await awaitingCascades(() =>
            Context!.PostJsonAsync<RegisterUser, User>(
                "/api/users",
                new RegisterUser(email, fullName, Password)));

        _secondCustomerId = registered.Body!.Id;

        await awaitingCascades(() =>
            Context!.PutJsonAsync<CompleteCustomer, Customer>(
                $"/api/customers/{_secondCustomerId}/complete",
                new CompleteCustomer(_secondCustomerId, fullName, fullName, "Poland")));

        (_secondWalletId, _) = await loadWallet(_secondCustomerId);
    }

    // ---- wallets ----------------------------------------------------------------------------

    [Given("I add {decimal} to the wallet")]
    public Task GivenAddFunds(decimal amount) => addFundsCore(amount);

    [When("I add {decimal} to the wallet")]
    public Task WhenAddFunds(decimal amount) => addFundsCore(amount);

    private async Task addFundsCore(decimal amount)
    {
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<AddFunds, Wallet>(
                $"/api/wallets/{_walletId}/funds/add",
                new AddFunds(_walletId, amount)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _wallet = result.Body;
    }

    [When("I transfer {decimal} to the second customer")]
    public async Task TransferToSecondCustomer(decimal amount)
    {
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<TransferFunds, Wallet>(
                "/api/wallets/transfer",
                new TransferFunds(_walletId, _secondWalletId, amount)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _wallet = result.Body;
    }

    // ---- deposits ---------------------------------------------------------------------------

    [When("I create a deposit of {decimal} {word}")]
    public async Task CreateDepositStep(decimal amount, string currency)
    {
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<global::Payments.CreateDeposit, Deposit>(
                "/api/payments/deposits",
                new global::Payments.CreateDeposit(_customerId, currency, amount)));

        _lastStatusCode = result.StatusCode;
        _deposit = result.Body;
    }

    // ---- assertions -------------------------------------------------------------------------

    [Check("the response status is {int}")]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Check("a customer profile exists for that user")]
    public async Task<bool> CustomerProfileExists()
    {
        var result = await Context!.GetJsonAsync<Customer>($"/api/customers/{_customerId}");
        return result.StatusCode == 200 && result.Body?.Id == _customerId;
    }

    [Check("the customer profile is marked complete")]
    public bool ProfileIsComplete() => _customer is { IsCompleted: true };

    [Check("the customer has {int} wallet")]
    public async Task<bool> CustomerHasNWallets(int expected)
    {
        var result = await Context!.GetJsonAsync<List<Wallet>>($"/api/wallets/owner/{_customerId}");
        return result.StatusCode == 200 && (result.Body?.Count ?? 0) == expected;
    }

    [Check("the wallet balance is {decimal}")]
    public bool WalletBalanceIs(decimal expected) => _wallet?.Balance == expected;

    [Check("the second customer's wallet balance is {decimal}")]
    public async Task<bool> SecondWalletBalanceIs(decimal expected)
    {
        var result = await Context!.GetJsonAsync<Wallet>($"/api/wallets/{_secondWalletId}");
        return result.StatusCode == 200 && result.Body?.Balance == expected;
    }

    [Check("the deposit is completed")]
    public bool DepositIsCompleted() => _deposit is { Status: DepositStatus.Completed };

    [Check("the deposit currency is {word}")]
    public bool DepositCurrencyIs(string expected) => _deposit?.Currency == expected;

    // ---- plumbing ---------------------------------------------------------------------------

    private async Task<(Guid, Wallet?)> loadWallet(Guid ownerId)
    {
        var result = await Context!.GetJsonAsync<List<Wallet>>($"/api/wallets/owner/{ownerId}");
        var wallet = result.Body?.FirstOrDefault();
        return (wallet?.Id ?? Guid.Empty, wallet);
    }

    /// <summary>
    /// Run an HTTP call and wait for every message it cascades to be fully handled.
    ///
    /// This is not belt-and-braces. The modules in this sample talk to each other over
    /// <c>UseDurableInbox()</c> local queues, so registering a user returns 200 *before* the
    /// Customers module has handled <c>UserCreated</c> and created the Customer, and completing
    /// a profile returns before the Wallets module has created the wallet. Asserting straight
    /// off the HTTP response would race the handler and fail intermittently — the kind of flake
    /// a spec suite is supposed to expose, not create.
    ///
    /// Measured, not assumed: replacing this with a plain <c>await call()</c> fails 3 of the 11
    /// scenarios outright.
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
