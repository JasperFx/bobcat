using System.Collections.Concurrent;

namespace CqrsMinimalApi;

public record Client(int Id, string Name, string Email);
public record CreateClientRequest(string Name, string Email);
public record UpdateClientRequest(string Name, string Email);

public static class ClientStore
{
    private static readonly ConcurrentDictionary<int, Client> _clients = new();
    private static int _nextId = 1;

    public static void Reset()
    {
        _clients.Clear();
        _nextId = 1;
    }

    public static Client Create(CreateClientRequest req)
    {
        var id = _nextId++;
        var client = new Client(id, req.Name, req.Email);
        _clients[id] = client;
        return client;
    }

    public static Client? GetById(int id) => _clients.TryGetValue(id, out var c) ? c : null;

    public static IEnumerable<Client> GetAll() => _clients.Values.OrderBy(c => c.Id);

    public static Client? Update(int id, UpdateClientRequest req)
    {
        if (!_clients.ContainsKey(id)) return null;
        var updated = new Client(id, req.Name, req.Email);
        _clients[id] = updated;
        return updated;
    }

    public static bool Delete(int id) => _clients.TryRemove(id, out _);
}

public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/clients", () => ClientStore.GetAll());

        app.MapPost("/api/clients", (CreateClientRequest req) =>
        {
            var client = ClientStore.Create(req);
            return Results.Created($"/api/clients/{client.Id}", client);
        });

        app.MapGet("/api/clients/{id:int}", (int id) =>
        {
            var client = ClientStore.GetById(id);
            return client is not null ? Results.Ok(client) : Results.NotFound();
        });

        app.MapPut("/api/clients/{id:int}", (int id, UpdateClientRequest req) =>
        {
            var client = ClientStore.Update(id, req);
            return client is not null ? Results.Ok(client) : Results.NotFound();
        });

        app.MapDelete("/api/clients/{id:int}", (int id) =>
        {
            var deleted = ClientStore.Delete(id);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }
}
