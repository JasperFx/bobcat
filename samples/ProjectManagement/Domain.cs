using System.Collections.Concurrent;

namespace ProjectManagement;

// Commands
public record CreateTask(string ProjectName, string Title, string AssignedTo);

// Domain model
public record ProjectTask(int Id, string ProjectName, string Title, string AssignedTo, string Status);

// In-memory store
public static class TaskStore
{
    private static readonly ConcurrentDictionary<int, ProjectTask> _tasks = new();
    private static int _nextId = 1;

    public static void Reset()
    {
        _tasks.Clear();
        _nextId = 1;
    }

    public static ProjectTask Add(string projectName, string title, string assignedTo)
    {
        var id = _nextId++;
        var task = new ProjectTask(id, projectName, title, assignedTo, "open");
        _tasks[id] = task;
        return task;
    }

    public static IReadOnlyList<ProjectTask> GetAll() => _tasks.Values.OrderBy(t => t.Id).ToList();
    public static ProjectTask? GetById(int id) => _tasks.TryGetValue(id, out var t) ? t : null;
}

// Wolverine handler
public class CreateTaskHandler
{
    public void Handle(CreateTask command)
    {
        TaskStore.Add(command.ProjectName, command.Title, command.AssignedTo);
    }
}

// AppBootstrap for HTTP API (list/get tasks via HTTP)
public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/tasks", () => TaskStore.GetAll());
        app.MapGet("/api/tasks/{id:int}", (int id) =>
        {
            var task = TaskStore.GetById(id);
            return task is not null ? Results.Ok(task) : Results.NotFound();
        });
    }
}
