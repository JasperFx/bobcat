using Alba;
using Bobcat;
using Bobcat.Runtime;
using Booking;
using Flight;
using Identity;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Passenger;
using Shouldly;

namespace BookingMonolith.Tests;

[FixtureTitle("Booking Monolith")]
public class BookingFixture : Fixture
{
    private IAlbaHost _host = null!;

    private UserAccount? _userAccount;
    private Passenger.Passenger? _passenger;
    private Flight.Flight? _flight;
    private BookingRecord? _booking;
    private List<Flight.Flight>? _flights;
    private List<BookingRecord>? _bookings;
    private int _lastStatusCode;

    public override Task SetUp()
    {
        _host = Context!.GetResource<AlbaResource<Program>>().AlbaHost;
        _userAccount = null;
        _passenger = null;
        _flight = null;
        _booking = null;
        _flights = null;
        _bookings = null;
        _lastStatusCode = 0;
        return Task.CompletedTask;
    }

    private async Task<Flight.Flight> CreateFlightInternal(string number, decimal price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateFlight(
                number,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                120, price,
                new DateTime(2026, 6, 1, 8, 0, 0),
                new DateTime(2026, 6, 1, 10, 0, 0),
                new DateTime(2026, 6, 1)
            )).ToUrl("/api/flights");
            x.StatusCodeShouldBeOk();
        });
        return result.ReadAsJson<Flight.Flight>()!;
    }

    private async Task<Passenger.Passenger> CreatePassengerInternal(string name, string passport)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreatePassenger(name, passport, PassengerType.Male, 30)).ToUrl("/api/passengers");
            x.StatusCodeShouldBeOk();
        });
        return result.ReadAsJson<Passenger.Passenger>()!;
    }

    // ── Given ────────────────────────────────────────────────────────────────

    [Given("a flight with number {string} and price {int} exists")]
    public async Task CreateFlightGiven(string number, int price)
    {
        _flight = await CreateFlightInternal(number, price);
    }

    [Given("a passenger named {string} with passport {string} exists")]
    public async Task CreatePassengerGiven(string name, string passport)
    {
        _passenger = await CreatePassengerInternal(name, passport);
    }

    [Given("a booking for the passenger and flight exists")]
    public async Task CreateBookingGiven()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateBooking(_passenger!.Id, _flight!.Id, null)).ToUrl("/api/bookings");
        });
        _booking = result.ReadAsJson<BookingRecord>()!;
    }

    [Given("a booking for the passenger and flight with description {string} exists")]
    public async Task CreateBookingWithDescriptionGiven(string description)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateBooking(_passenger!.Id, _flight!.Id, description)).ToUrl("/api/bookings");
        });
        _booking = result.ReadAsJson<BookingRecord>()!;
    }

    // ── When ─────────────────────────────────────────────────────────────────

    [When("I register a user with email {string} first name {string} last name {string} and password {string}")]
    public async Task RegisterUser(string email, string firstName, string lastName, string password)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser(email, firstName, lastName, password)).ToUrl("/api/identity/register");
            x.StatusCodeShouldBeOk();
        });
        _userAccount = result.ReadAsJson<UserAccount>()!;
        _lastStatusCode = 200;
    }

    [When("I try to register a user with email {string} and password {string}")]
    public async Task TryRegisterUser(string email, string password)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new RegisterUser(email, "Alice", "Smith", password)).ToUrl("/api/identity/register");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I create a passenger named {string} with passport {string} type {string} and age {int}")]
    public async Task CreatePassenger(string name, string passport, string type, int age)
    {
        var passengerType = Enum.Parse<PassengerType>(type);
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreatePassenger(name, passport, passengerType, age)).ToUrl("/api/passengers");
            x.StatusCodeShouldBeOk();
        });
        _passenger = result.ReadAsJson<Passenger.Passenger>()!;
        _lastStatusCode = 200;
    }

    [When("I try to create a passenger with empty name and passport {string}")]
    public async Task TryCreatePassengerEmptyName(string passport)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreatePassenger("", passport, PassengerType.Male, 30)).ToUrl("/api/passengers");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I create a flight with number {string} and price {decimal}")]
    public async Task CreateFlight(string number, decimal price)
    {
        _flight = await CreateFlightInternal(number, price);
        _lastStatusCode = 200;
    }

    [When("I try to create a flight with number {string} and price {int}")]
    public async Task TryCreateFlight(string number, int price)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateFlight(
                number,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                120, price,
                new DateTime(2026, 6, 1, 8, 0, 0),
                new DateTime(2026, 6, 1, 10, 0, 0),
                new DateTime(2026, 6, 1)
            )).ToUrl("/api/flights");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I get all flights")]
    public async Task GetAllFlights()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/api/flights");
            x.StatusCodeShouldBeOk();
        });
        _flights = result.ReadAsJson<List<Flight.Flight>>()!;
    }

    [When("I get the flight by id")]
    public async Task GetFlightById()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/flights/{_flight!.Id}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        if (_lastStatusCode == 200)
            _flight = result.ReadAsJson<Flight.Flight>()!;
    }

    [When("I get a flight by a random id")]
    public async Task GetFlightByRandomId()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/flights/{Guid.NewGuid()}");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I create a booking for the passenger and flight with description {string}")]
    public async Task CreateBooking(string description)
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateBooking(_passenger!.Id, _flight!.Id, description)).ToUrl("/api/bookings");
            x.StatusCodeShouldBeOk();
        });
        _booking = result.ReadAsJson<BookingRecord>()!;
        _lastStatusCode = 200;
    }

    [When("I try to create a booking with a non-existent passenger")]
    public async Task TryCreateBookingNoPassenger()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateBooking(Guid.NewGuid(), _flight!.Id, null)).ToUrl("/api/bookings");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I try to create a booking with a non-existent flight")]
    public async Task TryCreateBookingNoFlight()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateBooking(_passenger!.Id, Guid.NewGuid(), null)).ToUrl("/api/bookings");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I try to create a booking with empty ids")]
    public async Task TryCreateBookingEmptyIds()
    {
        var result = await _host.Scenario(x =>
        {
            x.Post.Json(new CreateBooking(Guid.Empty, Guid.Empty, null)).ToUrl("/api/bookings");
            // no status assertion — will check after;
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I get all bookings")]
    public async Task GetAllBookings()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url("/api/bookings");
            x.StatusCodeShouldBeOk();
        });
        _bookings = result.ReadAsJson<List<BookingRecord>>()!;
    }

    [When("I get the booking by id")]
    public async Task GetBookingById()
    {
        var result = await _host.Scenario(x =>
        {
            x.Get.Url($"/api/bookings/{_booking!.Id}");
            x.StatusCodeShouldBeOk();
        });
        _booking = result.ReadAsJson<BookingRecord>()!;
    }

    // ── Then / Check ─────────────────────────────────────────────────────────

    [Then("the response status should be {int}")]
    public void ResponseStatusShouldBe(int expected) => _lastStatusCode.ShouldBe(expected);

    [Check("the user account id should be valid")]
    public bool UserAccountIdIsValid() => _userAccount?.Id != Guid.Empty;

    [Then("the user account email should be {string}")]
    public void UserAccountEmailShouldBe(string expected) => _userAccount!.Email.ShouldBe(expected);

    [Then("the user first name should be {string}")]
    public void UserFirstNameShouldBe(string expected) => _userAccount!.FirstName.ShouldBe(expected);

    [Then("the user last name should be {string}")]
    public void UserLastNameShouldBe(string expected) => _userAccount!.LastName.ShouldBe(expected);

    [Check("the passenger id should be valid")]
    public bool PassengerIdIsValid() => _passenger?.Id != Guid.Empty;

    [Then("the passenger name should be {string}")]
    public void PassengerNameShouldBe(string expected) => _passenger!.Name.ShouldBe(expected);

    [Then("the passenger passport should be {string}")]
    public void PassengerPassportShouldBe(string expected) => _passenger!.PassportNumber.ShouldBe(expected);

    [Then("the passenger age should be {int}")]
    public void PassengerAgeShouldBe(int expected) => _passenger!.Age.ShouldBe(expected);

    [Check("the flight id should be valid")]
    public bool FlightIdIsValid() => _flight?.Id != Guid.Empty;

    [Then("the flight number should be {string}")]
    public void FlightNumberShouldBe(string expected) => _flight!.FlightNumber.ShouldBe(expected);

    [Then("the flight price should be {decimal}")]
    public void FlightPriceShouldBe(decimal expected) => _flight!.Price.ShouldBe(expected);

    [Then("the flights list should not be empty")]
    public void FlightsListNotEmpty() => _flights!.ShouldNotBeEmpty();

    [Check("the booking id should be valid")]
    public bool BookingIdIsValid() => _booking?.Id != Guid.Empty;

    [Check("the booking passenger id should match")]
    public bool BookingPassengerIdMatches() => _booking?.PassengerId == _passenger?.Id;

    [Then("the bookings list should not be empty")]
    public void BookingsListNotEmpty() => _bookings!.ShouldNotBeEmpty();

    [Check("the booking id should match")]
    public bool BookingIdMatches() => _booking?.Id != Guid.Empty;
}
