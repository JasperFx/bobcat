using Bobcat.Xunit.Samples.Scenarios;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The <c>docs/marker-steps.md</c> examples of Event Model roles read off a test body (bobcat#462): the
/// vocabulary is <see cref="SampleScenario"/>, and nothing in these tests is authored for the model. The
/// trigger's label is the one fact a body cannot state, so the class says it (bobcat#463). Green.
/// </summary>
// #region triggered-by
[BobcatFeature("Deposits"), BobcatScenario]
[BobcatSlice(SliceType = typeof(Deposit), TriggeredBy = "the account holder")]
public class DepositSpecs : SampleScenario
// #endregion triggered-by
{
    [Fact]
    public async Task a_deposit_is_credited_and_receipted()
    {
        var id = Guid.NewGuid();

        // #region roles-from-the-body
        await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));   // aggregate Account, arranged AccountOpened
        await WhenReceived(new Deposit(id, 10m));                        // the act: command Deposit
        ThenEvents(typeof(FundsDeposited));                              // emitted FundsDeposited
        ThenMessageSent<DepositReceipt>();                               // published DepositReceipt
        // #endregion roles-from-the-body
    }
}

/// <summary>
/// An act behind a helper of the project's own (bobcat#681), on a slice of its own so its HTTP trigger is
/// the only one the slice hears. Green.
/// </summary>
[BobcatFeature("Deposits over HTTP")]
public class PostedDepositSpecs : SampleScenario
{
    /// <summary>
    /// Declaring the attributes on the helper is what carries the trigger (verb and route) through to the
    /// slice; without them the call is BOBCAT045.
    /// </summary>
    [BobcatSpec(SliceName = "Post a deposit")]
    public async Task a_deposit_posted_through_a_helper()
    {
        var id = Guid.NewGuid();

        await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
        await PostTo(new Deposit(id, 10m), "/api/deposits");
        ThenEvents(typeof(FundsDeposited));
    }

    // #region helper-redeclares
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(url), HttpMethod = "POST")]
    protected Task PostTo(object command, string url)
        => WhenActing($"{command.GetType().Name} is posted to \"{url}\"", app => app.Handle(command));
    // #endregion helper-redeclares
}

/// <summary>A stream the act only looks up is context, not an aggregate (bobcat#502). Green.</summary>
[BobcatFeature("Opening accounts")]
public class OpenAccountSpecs : SampleScenario
{
    [BobcatSpec(typeof(OpenAccount))]
    public async Task an_enrolled_client_opens_an_account()
    {
        var clientId = Guid.NewGuid();

        // #region given-context
        await GivenContext<Client>(clientId, new ClientEnrolled(clientId, "Alice"));   // ReadsFrom Client
        await WhenPosted(new OpenAccount(clientId), "/api/accounts");                  // the act
        ThenStreamStarted<Account>();                                                   // aggregate Account
        // #endregion given-context
    }
}

/// <summary>A stored document is a lookup too, unless the slice is a View (bobcat#614). Green.</summary>
[BobcatFeature("Bookings")]
public class CreateBookingSpecs : SampleScenario
{
    [BobcatSpec(typeof(CreateBooking))]
    public async Task a_passenger_books_a_flight()
    {
        var passenger = new Passenger(Guid.NewGuid(), "Ann");
        var flight = new Flight(Guid.NewGuid(), "BC 101");

        // #region given-read-model
        await GivenReadModel<Passenger>(passenger);                       // ReadsFrom Passenger
        await GivenReadModel<Flight>(flight);                             // ReadsFrom Flight
        await WhenPosted(new CreateBooking(passenger.Id, flight.Id), "/api/bookings");   // a Command slice
        // #endregion given-read-model
    }
}
