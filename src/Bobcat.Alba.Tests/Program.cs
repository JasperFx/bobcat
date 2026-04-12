var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/api/hello", () => new HelloResponse("hello"));
app.MapPost("/api/echo", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Content(body, "application/json");
});
app.MapDelete("/api/items/{id}", (int id) => Results.NoContent());

app.Run();

public record HelloResponse(string Message);

// Expose Program for use with AlbaHost.For<Program>()
public partial class Program { }
