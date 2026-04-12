using System.Collections.Concurrent;

namespace BookingMonolith;

public record Room(int Id, string Number, string Type, decimal PricePerNight, bool Available);
public record CreateRoomRequest(string Number, string Type, decimal PricePerNight);

public record Booking(int Id, int RoomId, string GuestName, string CheckIn, string CheckOut, decimal TotalPrice, string Status);
public record CreateBookingRequest(int RoomId, string GuestName, string CheckIn, string CheckOut);

public static class Store
{
    private static readonly ConcurrentDictionary<int, Room> Rooms = new();
    private static readonly ConcurrentDictionary<int, Booking> Bookings = new();
    private static int _nextRoomId = 1;
    private static int _nextBookingId = 1;

    public static void Reset()
    {
        Rooms.Clear();
        Bookings.Clear();
        _nextRoomId = 1;
        _nextBookingId = 1;
    }

    public static Room CreateRoom(CreateRoomRequest req)
    {
        var id = _nextRoomId++;
        var room = new Room(id, req.Number, req.Type, req.PricePerNight, true);
        Rooms[id] = room;
        return room;
    }

    public static Room? GetRoom(int id) => Rooms.TryGetValue(id, out var r) ? r : null;

    public static IEnumerable<Room> GetAllRooms() => Rooms.Values.OrderBy(r => r.Id);

    public static IEnumerable<Room> GetAvailableRooms() => Rooms.Values.Where(r => r.Available).OrderBy(r => r.Id);

    public static Booking? CreateBooking(CreateBookingRequest req)
    {
        if (!Rooms.TryGetValue(req.RoomId, out var room) || !room.Available)
            return null;

        var checkIn = DateTime.Parse(req.CheckIn);
        var checkOut = DateTime.Parse(req.CheckOut);
        var nights = (checkOut - checkIn).Days;
        var total = room.PricePerNight * nights;

        var id = _nextBookingId++;
        var booking = new Booking(id, req.RoomId, req.GuestName, req.CheckIn, req.CheckOut, total, "confirmed");
        Bookings[id] = booking;

        // Mark room unavailable
        Rooms[req.RoomId] = room with { Available = false };
        return booking;
    }

    public static Booking? GetBooking(int id) => Bookings.TryGetValue(id, out var b) ? b : null;

    public static IEnumerable<Booking> GetAllBookings() => Bookings.Values.OrderBy(b => b.Id);

    public static IEnumerable<Booking> GetBookingsByGuest(string guestName) =>
        Bookings.Values.Where(b => b.GuestName.Equals(guestName, StringComparison.OrdinalIgnoreCase)).OrderBy(b => b.Id);

    public static Booking? CancelBooking(int id)
    {
        if (!Bookings.TryGetValue(id, out var booking))
            return null;

        var cancelled = booking with { Status = "cancelled" };
        Bookings[id] = cancelled;

        // Free up the room
        if (Rooms.TryGetValue(booking.RoomId, out var room))
            Rooms[booking.RoomId] = room with { Available = true };

        return cancelled;
    }
}

public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        // Rooms
        app.MapGet("/api/rooms", () => Store.GetAllRooms());

        app.MapPost("/api/rooms", (CreateRoomRequest req) =>
        {
            var room = Store.CreateRoom(req);
            return Results.Created($"/api/rooms/{room.Id}", room);
        });

        app.MapGet("/api/rooms/available", () => Store.GetAvailableRooms());

        app.MapGet("/api/rooms/{id:int}", (int id) =>
        {
            var room = Store.GetRoom(id);
            return room is not null ? Results.Ok(room) : Results.NotFound();
        });

        // Bookings
        app.MapGet("/api/bookings", (string? guestName) =>
        {
            if (!string.IsNullOrEmpty(guestName))
                return Results.Ok(Store.GetBookingsByGuest(guestName));
            return Results.Ok(Store.GetAllBookings());
        });

        app.MapPost("/api/bookings", (CreateBookingRequest req) =>
        {
            var booking = Store.CreateBooking(req);
            if (booking is null)
                return Results.BadRequest("Room not available or not found.");
            return Results.Created($"/api/bookings/{booking.Id}", booking);
        });

        app.MapGet("/api/bookings/{id:int}", (int id) =>
        {
            var booking = Store.GetBooking(id);
            return booking is not null ? Results.Ok(booking) : Results.NotFound();
        });

        app.MapDelete("/api/bookings/{id:int}", (int id) =>
        {
            var cancelled = Store.CancelBooking(id);
            return cancelled is not null ? Results.Ok(cancelled) : Results.NotFound();
        });
    }
}
