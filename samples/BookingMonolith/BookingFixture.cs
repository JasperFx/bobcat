using System.Text.Json;
using Alba;
using Bobcat;
using Bobcat.Runtime;

namespace BookingMonolith;

[FixtureTitle("Hotel Room Booking")]
public class BookingFixture : Fixture
{
    private IAlbaHost _host = null!;
    private int _lastStatusCode;
    private Room? _lastRoom;
    private Booking? _lastBooking;
    private int _currentRoomId;
    private int _currentBookingId;
    private List<Room> _lastRoomList = [];
    private List<Booking> _lastBookingList = [];

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public override Task SetUp()
    {
        _host = Context.GetResource<AlbaResource>().AlbaHost;
        return Task.CompletedTask;
    }

    // --- Room steps ---

    [When("I request all rooms")]
    public async Task RequestAllRooms()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/rooms"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastRoomList = JsonSerializer.Deserialize<List<Room>>(json, JsonOpts) ?? [];
    }

    [When("I request available rooms")]
    public async Task RequestAvailableRooms()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/rooms/available"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastRoomList = JsonSerializer.Deserialize<List<Room>>(json, JsonOpts) ?? [];
    }

    [When("I add a room with number {string} type {string} and price {int}")]
    public async Task AddRoom(string number, string type, int price)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { number, type, pricePerNight = (decimal)price }).ToUrl("/api/rooms");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastRoom = JsonSerializer.Deserialize<Room>(json, JsonOpts);
        if (_lastRoom is not null) _currentRoomId = _lastRoom.Id;
    }

    [Given("a room exists with number {string} type {string} and price {int}")]
    public async Task RoomExists(string number, string type, int price)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { number, type, pricePerNight = (decimal)price }).ToUrl("/api/rooms");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        _lastRoom = JsonSerializer.Deserialize<Room>(json, JsonOpts)!;
        _currentRoomId = _lastRoom.Id;
    }

    // --- Booking steps ---

    [When("I book the room for guest {string} from {string} to {string}")]
    public async Task BookRoom(string guestName, string checkIn, string checkOut)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { roomId = _currentRoomId, guestName, checkIn, checkOut }).ToUrl("/api/bookings");
            s.StatusCodeShouldBe(201);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastBooking = JsonSerializer.Deserialize<Booking>(json, JsonOpts);
        if (_lastBooking is not null) _currentBookingId = _lastBooking.Id;
    }

    [Given("a booking exists for guest {string} from {string} to {string}")]
    public async Task BookingExists(string guestName, string checkIn, string checkOut)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { roomId = _currentRoomId, guestName, checkIn, checkOut }).ToUrl("/api/bookings");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        _lastBooking = JsonSerializer.Deserialize<Booking>(json, JsonOpts)!;
        _currentBookingId = _lastBooking.Id;
    }

    [When("I get the booking details")]
    public async Task GetBookingDetails()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/bookings/{_currentBookingId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastBooking = JsonSerializer.Deserialize<Booking>(json, JsonOpts);
    }

    [When("I request all bookings")]
    public async Task RequestAllBookings()
    {
        var result = await _host.Scenario(s => s.Get.Url("/api/bookings"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastBookingList = JsonSerializer.Deserialize<List<Booking>>(json, JsonOpts) ?? [];
    }

    [When("I cancel the booking")]
    public async Task CancelBooking()
    {
        var result = await _host.Scenario(s =>
        {
            s.Delete.Url($"/api/bookings/{_currentBookingId}");
        });
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastBooking = JsonSerializer.Deserialize<Booking>(json, JsonOpts);
    }

    [When("I get a booking with id {int}")]
    public async Task GetBookingById(int id)
    {
        var result = await _host.Scenario(s =>
        {
            s.Get.Url($"/api/bookings/{id}");
            s.StatusCodeShouldBe(404);
        });
        _lastStatusCode = result.Context.Response.StatusCode;
    }

    [When("I search bookings by guest name {string}")]
    public async Task SearchByGuest(string guestName)
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/bookings?guestName={Uri.EscapeDataString(guestName)}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastBookingList = JsonSerializer.Deserialize<List<Booking>>(json, JsonOpts) ?? [];
    }

    [Given("another room exists with number {string} type {string} and price {int}")]
    public async Task AnotherRoomExists(string number, string type, int price)
    {
        var result = await _host.Scenario(s =>
        {
            s.Post.Json(new { number, type, pricePerNight = (decimal)price }).ToUrl("/api/rooms");
            s.StatusCodeShouldBe(201);
        });
        var json = await result.ReadAsTextAsync();
        var room = JsonSerializer.Deserialize<Room>(json, JsonOpts)!;
        _currentRoomId = room.Id;
    }

    // --- Then steps ---

    [Then("the response is 200 OK")]
    public void ResponseIs200() => AssertStatus(200);

    [Then("the response is 201 Created")]
    public void ResponseIs201() => AssertStatus(201);

    [Then("the room list is empty")]
    public void RoomListIsEmpty()
    {
        if (_lastRoomList.Count != 0)
            throw new Exception($"Expected empty room list but got {_lastRoomList.Count} rooms.");
    }

    [Then("the room list has {int} room")]
    public void RoomListHasOneRoom(int count) => RoomListHasCount(count);

    [Then("the room list has {int} rooms")]
    public void RoomListHasCount(int count)
    {
        if (_lastRoomList.Count != count)
            throw new Exception($"Expected {count} room(s) but got {_lastRoomList.Count}.");
    }

    [Then("the room number is {string}")]
    public void RoomNumberIs(string expected)
    {
        if (_lastRoom?.Number != expected)
            throw new Exception($"Expected room number '{expected}' but got '{_lastRoom?.Number}'.");
    }

    [Then("the room type is {string}")]
    public void RoomTypeIs(string expected)
    {
        if (_lastRoom?.Type != expected)
            throw new Exception($"Expected room type '{expected}' but got '{_lastRoom?.Type}'.");
    }

    [Then("the room is available")]
    public void RoomIsAvailable()
    {
        if (_lastRoom?.Available != true)
            throw new Exception("Expected room to be available.");
    }

    [Then("the room is not available")]
    public void RoomIsNotAvailable()
    {
        if (_lastRoom?.Available != false)
            throw new Exception("Expected room to not be available.");
    }

    [Then("the booking guest is {string}")]
    public void BookingGuestIs(string expected)
    {
        if (_lastBooking?.GuestName != expected)
            throw new Exception($"Expected guest '{expected}' but got '{_lastBooking?.GuestName}'.");
    }

    [Then("the booking status is {string}")]
    public void BookingStatusIs(string expected)
    {
        if (_lastBooking?.Status != expected)
            throw new Exception($"Expected booking status '{expected}' but got '{_lastBooking?.Status}'.");
    }

    [Then("the booking list has {int} booking")]
    public void BookingListHasOneBooking(int count) => BookingListHasCount(count);

    [Then("the booking list has {int} bookings")]
    public void BookingListHasCount(int count)
    {
        if (_lastBookingList.Count != count)
            throw new Exception($"Expected {count} booking(s) but got {_lastBookingList.Count}.");
    }

    [Then("the response is 404 Not Found")]
    public void ResponseIs404() => AssertStatus(404);

    [When("I check the room availability")]
    public async Task CheckRoomAvailability()
    {
        var result = await _host.Scenario(s => s.Get.Url($"/api/rooms/{_currentRoomId}"));
        _lastStatusCode = result.Context.Response.StatusCode;
        var json = await result.ReadAsTextAsync();
        _lastRoom = JsonSerializer.Deserialize<Room>(json, JsonOpts);
    }

    private void AssertStatus(int expected)
    {
        if (_lastStatusCode != expected)
            throw new Exception($"Expected HTTP {expected} but got {_lastStatusCode}.");
    }
}
