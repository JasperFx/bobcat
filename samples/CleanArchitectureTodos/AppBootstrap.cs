using System.Collections.Concurrent;

namespace CleanArchitectureTodos;

public record TodoItem(int Id, string Title, string? Description, bool Completed);
public record CreateTodoRequest(string Title, string? Description);
public record UpdateTodoRequest(string Title, string? Description);

public static class TodoStore
{
    private static readonly ConcurrentDictionary<int, TodoItem> _todos = new();
    private static int _nextId = 1;

    public static void Reset()
    {
        _todos.Clear();
        _nextId = 1;
    }

    public static TodoItem Create(CreateTodoRequest req)
    {
        var id = _nextId++;
        var todo = new TodoItem(id, req.Title, req.Description, false);
        _todos[id] = todo;
        return todo;
    }

    public static TodoItem? GetById(int id) => _todos.TryGetValue(id, out var t) ? t : null;

    public static IEnumerable<TodoItem> GetAll(bool? completed = null)
    {
        var all = _todos.Values.OrderBy(t => t.Id);
        return completed.HasValue ? all.Where(t => t.Completed == completed.Value) : all;
    }

    public static TodoItem? Update(int id, UpdateTodoRequest req)
    {
        if (!_todos.TryGetValue(id, out var existing)) return null;
        var updated = existing with { Title = req.Title, Description = req.Description };
        _todos[id] = updated;
        return updated;
    }

    public static TodoItem? Complete(int id)
    {
        if (!_todos.TryGetValue(id, out var existing)) return null;
        var completed = existing with { Completed = true };
        _todos[id] = completed;
        return completed;
    }

    public static bool Delete(int id) => _todos.TryRemove(id, out _);
}

public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/todos", (bool? completed) => TodoStore.GetAll(completed));

        app.MapPost("/api/todos", (CreateTodoRequest req) =>
        {
            var todo = TodoStore.Create(req);
            return Results.Created($"/api/todos/{todo.Id}", todo);
        });

        app.MapGet("/api/todos/{id:int}", (int id) =>
        {
            var todo = TodoStore.GetById(id);
            return todo is not null ? Results.Ok(todo) : Results.NotFound();
        });

        app.MapPut("/api/todos/{id:int}", (int id, UpdateTodoRequest req) =>
        {
            var todo = TodoStore.Update(id, req);
            return todo is not null ? Results.Ok(todo) : Results.NotFound();
        });

        app.MapDelete("/api/todos/{id:int}", (int id) =>
        {
            var deleted = TodoStore.Delete(id);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        app.MapMethods("/api/todos/{id:int}/complete", ["PATCH"], (int id) =>
        {
            var todo = TodoStore.Complete(id);
            return todo is not null ? Results.Ok(todo) : Results.NotFound();
        });
    }
}
