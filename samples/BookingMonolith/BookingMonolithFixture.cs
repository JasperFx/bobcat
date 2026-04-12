using Bobcat;
using Bobcat.Alba;

namespace BookingMonolith.Tests;

[FixtureTitle("Booking Monolith")]
public class BookingMonolithFixture
{
    private Guid _userId;
    private Guid _passengerId;
    private Guid _flightId;
    private Guid _bookingId;
    private int _lastStatusCode;
    private List<FlightDto> _flights = [];
    private List<BookingDto> _bookings = [];

    [When("I register a user with email {string} and password {string}")]
    [Given("I register a user with email {string} and password {string}")]
    public async Task RegisterUser(IStepContext context, string email, string password)
    {
        var result = await context.PostJsonAsync<RegisterUserRequest, RegisterUserResponse>(
            "/api/users/register",
            new RegisterUserRequest(email, password));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _userId = result.Body.UserId;
    }

    [When("I create a passenger with name {string} and age {int}")]
    [Given("I create a passenger with name {string} and age {int}")]
    public async Task CreatePassenger(IStepContext context, string name, int age)
    {
        var result = await context.PostJsonAsync<CreatePassengerRequest, CreatePassengerResponse>(
            "/api/passengers",
            new CreatePassengerRequest(_userId, name, age));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _passengerId = result.Body.PassengerId;
    }

    [When("I create a flight from {string} to {string} with price {float}")]
    [Given("I create a flight from {string} to {string} with price {float}")]
    public async Task CreateFlight(IStepContext context, string from, string to, float price)
    {
        var result = await context.PostJsonAsync<CreateFlightRequest, CreateFlightResponse>(
            "/api/flights",
            new CreateFlightRequest(from, to, (decimal)price, DateTime.UtcNow.AddDays(30)));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _flightId = result.Body.FlightId;
    }

    [When("I get all flights")]
    public async Task GetAllFlights(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<FlightDto>>("/api/flights");
        _lastStatusCode = result.StatusCode;
        _flights = result.Body ?? [];
    }

    [When("I get the flight by id")]
    public async Task GetFlightById(IStepContext context)
    {
        var result = await context.GetJsonAsync<FlightDto>($"/api/flights/{_flightId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I get flight by id {string}")]
    public async Task GetFlightByStringId(IStepContext context, string id)
    {
        var result = await context.GetJsonAsync<FlightDto>($"/api/flights/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("I create a booking")]
    [Given("I create a booking")]
    public async Task CreateBooking(IStepContext context)
    {
        var result = await context.PostJsonAsync<CreateBookingRequest, CreateBookingResponse>(
            "/api/bookings",
            new CreateBookingRequest(_userId, _passengerId, _flightId));
        _lastStatusCode = result.StatusCode;
        if (result.Body is not null)
            _bookingId = result.Body.BookingId;
    }

    [When("I create a booking with missing passenger")]
    public async Task CreateBookingMissingPassenger(IStepContext context)
    {
        var result = await context.PostJsonAsync<CreateBookingRequest, object>(
            "/api/bookings",
            new CreateBookingRequest(_userId, Guid.Empty, _flightId));
        _lastStatusCode = result.StatusCode;
    }

    [When("I create a booking with missing flight")]
    public async Task CreateBookingMissingFlight(IStepContext context)
    {
        var result = await context.PostJsonAsync<CreateBookingRequest, object>(
            "/api/bookings",
            new CreateBookingRequest(_userId, _passengerId, Guid.Empty));
        _lastStatusCode = result.StatusCode;
    }

    [When("I get all bookings")]
    public async Task GetAllBookings(IStepContext context)
    {
        var result = await context.GetJsonAsync<List<BookingDto>>("/api/bookings");
        _lastStatusCode = result.StatusCode;
        _bookings = result.Body ?? [];
    }

    [When("I get the booking by id")]
    public async Task GetBookingById(IStepContext context)
    {
        var result = await context.GetJsonAsync<BookingDto>($"/api/bookings/{_bookingId}");
        _lastStatusCode = result.StatusCode;
    }

    [Then("the response status is {int}")]
    [Check]
    public bool StatusIs(int expected) => _lastStatusCode == expected;

    [Then("the user id is returned")]
    [Check]
    public bool UserIdReturned() => _userId != Guid.Empty;

    [Then("the passenger id is returned")]
    [Check]
    public bool PassengerIdReturned() => _passengerId != Guid.Empty;

    [Then("the flight id is returned")]
    [Check]
    public bool FlightIdReturned() => _flightId != Guid.Empty;

    [Then("at least {int} flight is returned")]
    [Check]
    public bool AtLeastNFlights(int min) => _flights.Count >= min;

    [Then("at least {int} booking is returned")]
    [Check]
    public bool AtLeastNBookings(int min) => _bookings.Count >= min;
}

record RegisterUserRequest(string Email, string Password);
record RegisterUserResponse(Guid UserId);
record CreatePassengerRequest(Guid UserId, string Name, int Age);
record CreatePassengerResponse(Guid PassengerId);
record CreateFlightRequest(string DepartureAirport, string ArrivalAirport, decimal Price, DateTime DepartureDate);
record CreateFlightResponse(Guid FlightId);
record FlightDto(Guid Id, string From, string To, decimal Price);
record CreateBookingRequest(Guid UserId, Guid PassengerId, Guid FlightId);
record CreateBookingResponse(Guid BookingId);
record BookingDto(Guid Id, Guid FlightId, Guid PassengerId);
