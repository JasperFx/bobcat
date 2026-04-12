using System.Collections.Concurrent;

namespace MeetingGroupMonolith;

public record Group(int Id, string Name, string Description, string City, int MemberCount);
public record CreateGroupRequest(string Name, string Description, string City);

public record Event(int Id, int GroupId, string Title, string Date, string Location, int MaxAttendees, int AttendeeCount);
public record CreateEventRequest(string Title, string Date, string Location, int MaxAttendees);

public record Member(int Id, int GroupId, string MemberName, string JoinedAt);
public record CreateMemberRequest(string MemberName);

public static class Store
{
    private static readonly ConcurrentDictionary<int, Group> Groups = new();
    private static readonly ConcurrentDictionary<int, Event> Events = new();
    private static readonly ConcurrentDictionary<int, Member> Members = new();
    private static int _nextGroupId = 1;
    private static int _nextEventId = 1;
    private static int _nextMemberId = 1;

    public static void Reset()
    {
        Groups.Clear();
        Events.Clear();
        Members.Clear();
        _nextGroupId = 1;
        _nextEventId = 1;
        _nextMemberId = 1;
    }

    public static Group CreateGroup(CreateGroupRequest req)
    {
        var id = _nextGroupId++;
        var group = new Group(id, req.Name, req.Description, req.City, 0);
        Groups[id] = group;
        return group;
    }

    public static Group? GetGroup(int id) => Groups.TryGetValue(id, out var g) ? g : null;

    public static IEnumerable<Group> GetAllGroups() => Groups.Values.OrderBy(g => g.Id);

    public static Member? AddMember(int groupId, CreateMemberRequest req)
    {
        if (!Groups.TryGetValue(groupId, out var group))
            return null;
        var id = _nextMemberId++;
        var member = new Member(id, groupId, req.MemberName, DateTime.UtcNow.ToString("O"));
        Members[id] = member;
        Groups[groupId] = group with { MemberCount = group.MemberCount + 1 };
        return member;
    }

    public static IEnumerable<Member> GetGroupMembers(int groupId) =>
        Members.Values.Where(m => m.GroupId == groupId).OrderBy(m => m.Id);

    public static Event? CreateEvent(int groupId, CreateEventRequest req)
    {
        if (!Groups.ContainsKey(groupId))
            return null;
        var id = _nextEventId++;
        var evt = new Event(id, groupId, req.Title, req.Date, req.Location, req.MaxAttendees, 0);
        Events[id] = evt;
        return evt;
    }

    public static IEnumerable<Event> GetGroupEvents(int groupId) =>
        Events.Values.Where(e => e.GroupId == groupId).OrderBy(e => e.Id);

    public static (bool Success, bool Full) AttendEvent(int groupId, int eventId)
    {
        if (!Events.TryGetValue(eventId, out var evt) || evt.GroupId != groupId)
            return (false, false);
        if (evt.AttendeeCount >= evt.MaxAttendees)
            return (false, true);
        Events[eventId] = evt with { AttendeeCount = evt.AttendeeCount + 1 };
        return (true, false);
    }
}

public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        // Groups
        app.MapPost("/api/groups", (CreateGroupRequest req) =>
        {
            var group = Store.CreateGroup(req);
            return Results.Created($"/api/groups/{group.Id}", group);
        });

        app.MapGet("/api/groups", () => Store.GetAllGroups());

        app.MapGet("/api/groups/{id:int}", (int id) =>
        {
            var group = Store.GetGroup(id);
            return group is not null ? Results.Ok(group) : Results.NotFound();
        });

        // Members
        app.MapPost("/api/groups/{id:int}/members", (int id, CreateMemberRequest req) =>
        {
            var member = Store.AddMember(id, req);
            if (member is null) return Results.NotFound();
            return Results.Created($"/api/groups/{id}/members/{member.Id}", member);
        });

        app.MapGet("/api/groups/{id:int}/members", (int id) =>
            Results.Ok(Store.GetGroupMembers(id)));

        // Events
        app.MapPost("/api/groups/{id:int}/events", (int id, CreateEventRequest req) =>
        {
            var evt = Store.CreateEvent(id, req);
            if (evt is null) return Results.NotFound();
            return Results.Created($"/api/groups/{id}/events/{evt.Id}", evt);
        });

        app.MapGet("/api/groups/{id:int}/events", (int id) =>
            Results.Ok(Store.GetGroupEvents(id)));

        app.MapPost("/api/groups/{groupId:int}/events/{eventId:int}/attend", (int groupId, int eventId) =>
        {
            var (success, full) = Store.AttendEvent(groupId, eventId);
            if (!success && full) return Results.BadRequest("Event is full.");
            if (!success) return Results.NotFound();
            return Results.Ok();
        });
    }
}
