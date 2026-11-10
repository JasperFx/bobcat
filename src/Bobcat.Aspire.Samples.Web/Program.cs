var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/greeting/{name}", (string name) => $"Hello, {name}");

// Work that finishes after the request returns: POST starts it, GET reports it once done. This is
// what an "eventually" step is for.
var jobs = new Dictionary<string, DateTime>();
app.MapPost("/jobs/{id}", (string id) =>
{
    lock (jobs) jobs[id] = DateTime.UtcNow.AddMilliseconds(500);
    return Results.Accepted();
});
app.MapGet("/jobs/{id}", (string id) =>
{
    lock (jobs)
        return jobs.TryGetValue(id, out var doneAt) && DateTime.UtcNow >= doneAt
            ? Results.Ok("done")
            : Results.NotFound();
});

app.Run();
