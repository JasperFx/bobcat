using System.Collections.Concurrent;

namespace MoreSpeakers;

public record Speaker(int Id, string Name, string Bio, string Topic);
public record CreateSpeakerRequest(string Name, string Bio, string Topic);
public record UpdateSpeakerRequest(string Name, string Bio, string Topic);
public record Session(int Id, int SpeakerId, string Title, int DurationMinutes);
public record CreateSessionRequest(string Title, int DurationMinutes);

public static class Store
{
    private static readonly ConcurrentDictionary<int, Speaker> _speakers = new();
    private static readonly ConcurrentDictionary<int, Session> _sessions = new();
    private static int _nextSpeakerId = 1;
    private static int _nextSessionId = 1;

    public static void Reset()
    {
        _speakers.Clear();
        _sessions.Clear();
        _nextSpeakerId = 1;
        _nextSessionId = 1;
    }

    public static Speaker CreateSpeaker(CreateSpeakerRequest req)
    {
        var id = _nextSpeakerId++;
        var speaker = new Speaker(id, req.Name, req.Bio, req.Topic);
        _speakers[id] = speaker;
        return speaker;
    }

    public static Speaker? GetSpeakerById(int id) => _speakers.TryGetValue(id, out var s) ? s : null;

    public static IEnumerable<Speaker> GetAllSpeakers() => _speakers.Values.OrderBy(s => s.Id);

    public static Speaker? UpdateSpeaker(int id, UpdateSpeakerRequest req)
    {
        if (!_speakers.ContainsKey(id)) return null;
        var updated = new Speaker(id, req.Name, req.Bio, req.Topic);
        _speakers[id] = updated;
        return updated;
    }

    public static bool DeleteSpeaker(int id)
    {
        _sessions.Values.Where(s => s.SpeakerId == id).ToList()
            .ForEach(s => _sessions.TryRemove(s.Id, out _));
        return _speakers.TryRemove(id, out _);
    }

    public static Session? CreateSession(int speakerId, CreateSessionRequest req)
    {
        if (!_speakers.ContainsKey(speakerId)) return null;
        var id = _nextSessionId++;
        var session = new Session(id, speakerId, req.Title, req.DurationMinutes);
        _sessions[id] = session;
        return session;
    }

    public static IEnumerable<Session>? GetSessionsForSpeaker(int speakerId)
    {
        if (!_speakers.ContainsKey(speakerId)) return null;
        return _sessions.Values.Where(s => s.SpeakerId == speakerId).OrderBy(s => s.Id);
    }
}

public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/speakers", () => Store.GetAllSpeakers());

        app.MapPost("/api/speakers", (CreateSpeakerRequest req) =>
        {
            var speaker = Store.CreateSpeaker(req);
            return Results.Created($"/api/speakers/{speaker.Id}", speaker);
        });

        app.MapGet("/api/speakers/{id:int}", (int id) =>
        {
            var speaker = Store.GetSpeakerById(id);
            return speaker is not null ? Results.Ok(speaker) : Results.NotFound();
        });

        app.MapPut("/api/speakers/{id:int}", (int id, UpdateSpeakerRequest req) =>
        {
            var speaker = Store.UpdateSpeaker(id, req);
            return speaker is not null ? Results.Ok(speaker) : Results.NotFound();
        });

        app.MapDelete("/api/speakers/{id:int}", (int id) =>
        {
            var deleted = Store.DeleteSpeaker(id);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/speakers/{id:int}/sessions", (int id, CreateSessionRequest req) =>
        {
            var session = Store.CreateSession(id, req);
            return session is not null
                ? Results.Created($"/api/speakers/{id}/sessions/{session.Id}", session)
                : Results.NotFound();
        });

        app.MapGet("/api/speakers/{id:int}/sessions", (int id) =>
        {
            var sessions = Store.GetSessionsForSpeaker(id);
            return sessions is not null ? Results.Ok(sessions) : Results.NotFound();
        });
    }
}
