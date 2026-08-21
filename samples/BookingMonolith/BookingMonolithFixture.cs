using Bobcat;
using Bobcat.Alba;
using Booking;
using Identity;
using Wolverine;
using Wolverine.Tracking;

namespace BookingMonolith.Tests;

/// <summary>
/// Specs for the booking-modular-monolith conversion. Reuses the host's own command and document
/// types via the project reference rather than re-declaring request/response records locally —
/// the file this replaced posted <c>RegisterUserRequest(Email, Password)</c> to
/// <c>/api/users/register</c> against a host whose endpoint is <c>/api/identity/register</c> and
/// whose command also requires a first and last name; posted a <c>CreatePassengerRequest</c>
/// carrying a <c>UserId</c> to an endpoint that takes none and requires a passport number
/// instead; and described flights by departure and arrival airport codes when the host stores
/// only airport ids. Nothing had ever compiled it, so nothing reported any of that.
/// </summary>
[FixtureTitle("Booking Monolith")]
public class BookingMonolithFixture : Fixture
{
    // RegisterUser requires a first and last name, which the spec's registration step does not
    // spell out; the names are not what any scenario is about.
    private const string FirstName = "Spec";
    private const string LastName = "User";
    private const string PassportNumber = "SPEC-0001";

    private Guid _userId;
    private Guid _passengerId;
    private Guid _flightId;
    private Guid _bookingId;
    private int _lastStatusCode;
    private List<global::Flight.Flight> _flights = [];
    private List<BookingRecord> _bookings = [];

    public Task BeforeEach()
    {
        _userId = Guid.Empty;
        _passengerId = Guid.Empty;
        _flightId = Guid.Empty;
        _bookingId = Guid.Empty;
        _lastStatusCode = 0;
        _flights = [];
        _bookings = [];
        return Task.CompletedTask;
    }

    // ---- identity ---------------------------------------------------------------------------

    [Given("I register a user with email {string} and password {string}")]
    public Task GivenRegister(string email, string password) => registerCore(email, password);

    [When("I register a user with email {string} and password {string}")]
    public Task WhenRegister(string email, string password) => registerCore(email, password);

    private async Task registerCore(string email, string password)
    {
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<RegisterUser, UserAccount>(
                "/api/identity/register",
                new RegisterUser(email, FirstName, LastName, password)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _userId = result.Body.Id;
    }

    // ---- passengers -------------------------------------------------------------------------

    [Given("I create a passenger named {string} aged {int}")]
    public Task GivenCreatePassenger(string name, int age) => createPassengerCore(name, age);

    [When("I create a passenger named {string} aged {int}")]
    public Task WhenCreatePassenger(string name, int age) => createPassengerCore(name, age);

    private async Task createPassengerCore(string name, int age)
    {
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<global::Passenger.CreatePassenger, global::Passenger.Passenger>(
                "/api/passengers",
                new global::Passenger.CreatePassenger(name, PassportNumber, global::Passenger.PassengerType.Unknown, age)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _passengerId = result.Body.Id;
    }

    // ---- flights ----------------------------------------------------------------------------

    [Given("I create flight {string} priced {decimal}")]
    public Task GivenCreateFlight(string flightNumber, decimal price) => createFlightCore(flightNumber, price);

    [When("I create flight {string} priced {decimal}")]
    public Task WhenCreateFlight(string flightNumber, decimal price) => createFlightCore(flightNumber, price);

    private async Task createFlightCore(string flightNumber, decimal price)
    {
        // The host has no airport or aircraft endpoints, so those references are opaque ids as
        // far as these specs can tell; the flight number is the only human-readable identity a
        // flight carries, which is why the steps name flights by it.
        var departs = DateTime.UtcNow.AddDays(30);
        var command = new global::Flight.CreateFlight(
            FlightNumber: flightNumber,
            AircraftId: Guid.NewGuid(),
            DepartureAirportId: Guid.NewGuid(),
            ArriveAirportId: Guid.NewGuid(),
            DurationMinutes: 300,
            Price: price,
            DepartureDate: departs,
            ArriveDate: departs.AddHours(5),
            FlightDate: departs);

        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<global::Flight.CreateFlight, global::Flight.Flight>("/api/flights", command));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _flightId = result.Body.Id;
    }

    [When("I get all flights")]
    public async Task GetAllFlights()
    {
        var result = await Context!.GetJsonAsync<List<global::Flight.Flight>>("/api/flights");
        _lastStatusCode = result.StatusCode;
        _flights = result.Body ?? [];
    }

    [When("I get the flight by id")]
    public async Task GetFlightById()
    {
        var result = await Context!.GetJsonAsync<global::Flight.Flight>($"/api/flights/{_flightId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I get flight by id {string}")]
    public async Task GetFlightByStringId(string id)
    {
        var result = await Context!.GetJsonAsync<global::Flight.Flight>($"/api/flights/{id}");
        _lastStatusCode = result.StatusCode;
    }

    // ---- bookings ---------------------------------------------------------------------------

    [Given("I book the flight for the passenger")]
    public Task GivenBook() => bookCore(_passengerId, _flightId);

    [When("I book the flight for the passenger")]
    public Task WhenBook() => bookCore(_passengerId, _flightId);

    // Fresh ids rather than Guid.Empty: an empty id is rejected by the command's validator before
    // the endpoint runs, whereas an id nobody has stored exercises the [Entity] OnMissing path the
    // endpoint advertises — missing referenced entities are bad input, so 400 rather than 404.
    [When("I book the flight for a passenger that does not exist")]
    public Task BookForUnknownPassenger() => bookCore(Guid.NewGuid(), _flightId);

    [When("I book a flight that does not exist for the passenger")]
    public Task BookUnknownFlight() => bookCore(_passengerId, Guid.NewGuid());

    private async Task bookCore(Guid passengerId, Guid flightId)
    {
        var result = await awaitingCascades(() =>
            Context!.PostJsonAsync<CreateBooking, BookingRecord>(
                "/api/bookings",
                new CreateBooking(passengerId, flightId, Description: null)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _bookingId = result.Body.Id;
    }

    [When("I get all bookings")]
    public async Task GetAllBookings()
    {
        var result = await Context!.GetJsonAsync<List<BookingRecord>>("/api/bookings");
        _lastStatusCode = result.StatusCode;
        _bookings = result.Body ?? [];
    }

    [When("I get the booking by id")]
    public async Task GetBookingById()
    {
        var result = await Context!.GetJsonAsync<BookingRecord>($"/api/bookings/{_bookingId}");
        _lastStatusCode = result.StatusCode;
    }

    // ---- assertions -------------------------------------------------------------------------

    [Check("the response status is {int}")]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Check("the stored user has email {string}")]
    public async Task<bool> StoredUserHasEmail(string email)
    {
        var result = await Context!.GetJsonAsync<UserAccount>($"/api/identity/{_userId}");
        return result.StatusCode == 200 && result.Body?.Email == email;
    }

    /// <summary>
    /// The stub is written by the Passenger module's UserCreated handler, off a durable local
    /// queue, under the user's own id — so reading it back is the only way to observe that the
    /// cascade between the two modules actually arrived.
    /// </summary>
    [Check("a passenger stub exists for that user")]
    public async Task<bool> PassengerStubExists()
    {
        var result = await Context!.GetJsonAsync<global::Passenger.Passenger>($"/api/passengers/{_userId}");
        return result.StatusCode == 200 && result.Body?.Id == _userId;
    }

    [Check("the stored passenger is named {string} aged {int}")]
    public async Task<bool> StoredPassengerIs(string name, int age)
    {
        var result = await Context!.GetJsonAsync<global::Passenger.Passenger>($"/api/passengers/{_passengerId}");
        return result.StatusCode == 200 && result.Body?.Name == name && result.Body?.Age == age;
    }

    [Check("the stored flight is {string} priced {decimal}")]
    public async Task<bool> StoredFlightIs(string flightNumber, decimal price)
    {
        var result = await Context!.GetJsonAsync<global::Flight.Flight>($"/api/flights/{_flightId}");
        return result.StatusCode == 200
            && result.Body?.FlightNumber == flightNumber
            && result.Body?.Price == price;
    }

    [Check("the flights include {string}")]
    public bool FlightsInclude(string flightNumber) => _flights.Any(f => f.FlightNumber == flightNumber);

    /// <summary>
    /// Reads the booking back rather than trusting the write's response body — the point of the
    /// Booking module is that the record is rebuilt from its event stream, so asserting against
    /// what the POST echoed would test nothing.
    /// </summary>
    [Check("the stored booking is for {string} on flight {string} priced {decimal}")]
    public async Task<bool> StoredBookingIs(string passengerName, string flightNumber, decimal price)
    {
        var result = await Context!.GetJsonAsync<BookingRecord>($"/api/bookings/{_bookingId}");
        return result.StatusCode == 200
            && result.Body?.PassengerName == passengerName
            && result.Body?.FlightNumber == flightNumber
            && result.Body?.Price == price;
    }

    [Check("the bookings include the new booking")]
    public bool BookingsIncludeNew() => _bookings.Any(b => b.Id == _bookingId);

    // ---- plumbing ---------------------------------------------------------------------------

    /// <summary>
    /// Run an HTTP call and wait for every message it cascades to be fully handled.
    ///
    /// Every write in this host cascades an integration event onto a <c>UseDurableInbox()</c>
    /// local queue, and one of them matters: registering a user returns 201 *before* the
    /// Passenger module has handled <c>UserCreated</c> and stored the stub, so asserting the stub
    /// exists straight off the HTTP response races the handler. The rest (PassengerCreated,
    /// FlightCreated, BookingCreated) have no handler today; waiting for them anyway means no
    /// scenario leaves a message in flight for the next scenario's reset to land on.
    /// See docs/sample-wiring.md footgun 7.
    ///
    /// Measured, and recorded so nobody has to re-derive it: unlike PaymentsMonolith, replacing
    /// this with a plain <c>await call()</c> did NOT fail — 10 runs out of 10 were green, because
    /// the stub handler is one document write and usually beats the follow-up GET. That is a race
    /// being won, not the absence of one, and a spec that depends on winning it is the flake this
    /// suite exists to expose rather than create. So it stays.
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
