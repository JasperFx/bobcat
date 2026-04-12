using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Bobcat.Alba.Tests.TestApp;

/// <summary>
/// Factory for the test web app AlbaResource. Uses AlbaHost.For(builder, configureRoutes)
/// so no entry point conflict with the xUnit test runner.
/// </summary>
public static class TestWebApp
{
    public const string ResourceName = "TestWebApp";

    public static AlbaResource Create()
        => AlbaResource.For(ResourceName, app =>
        {
            app.MapGet("/api/hello", () => new HelloResponse("hello"));
            app.MapPost("/api/echo", async (HttpContext ctx) =>
            {
                var request = await ctx.Request.ReadFromJsonAsync<EchoRequest>();
                return Results.Ok(new EchoResponse(request!.Message));
            });
            app.MapDelete("/api/items/{id}", (string id) => Results.NoContent());
            app.MapGet("/api/items/{id}", (string id) => new ItemResponse(id, $"Item {id}"));
        });
}

public record HelloResponse(string Message);
public record EchoRequest(string Message);
public record EchoResponse(string Message);
public record ItemResponse(string Id, string Name);
