using Alba;
using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Bobcat.Alba.Tests;

/// <summary>
/// Covers the IStepContext HTTP helpers a fixture actually calls. These were untested until
/// PutJsonAsync was added for the PaymentsMonolith sample, whose profile-completion endpoint is
/// a WolverinePut — there was no way to express a PUT from a spec at all.
/// </summary>
public class AlbaStepContextExtensionsTests : IAsyncLifetime
{
    private readonly TestSuite _suite = new();
    private SpecExecutionContext _context = null!;

    public async ValueTask InitializeAsync()
    {
        _suite.AddResource(new AlbaResource(() => AlbaHost.For(WebApplication.CreateBuilder(), app =>
        {
            app.MapGet("/thing", () => new Thing("read", 1));
            app.MapPost("/thing", (Thing body) => Results.Json(body with { Name = body.Name + "-posted" }));
            app.MapPut("/thing", (Thing body) => Results.Json(body with { Name = body.Name + "-put" }));
            app.MapPut("/conflict", () => Results.StatusCode(409));
            app.MapDelete("/thing", () => Results.NoContent());
        })));

        await _suite.StartAll();
        _context = new SpecExecutionContext("spec", suite: _suite);
    }

    public async ValueTask DisposeAsync() => await _suite.DisposeAsync();

    [Fact]
    public async Task get_returns_status_and_deserialized_body()
    {
        var result = await _context.GetJsonAsync<Thing>("/thing");

        result.StatusCode.ShouldBe(200);
        result.Body!.Name.ShouldBe("read");
    }

    [Fact]
    public async Task post_sends_the_body_and_reads_the_response()
    {
        var result = await _context.PostJsonAsync<Thing, Thing>("/thing", new Thing("sent", 2));

        result.StatusCode.ShouldBe(200);
        result.Body!.Name.ShouldBe("sent-posted");
    }

    [Fact]
    public async Task put_sends_the_body_and_reads_the_response()
    {
        var result = await _context.PutJsonAsync<Thing, Thing>("/thing", new Thing("sent", 3));

        result.StatusCode.ShouldBe(200);
        result.Body!.Name.ShouldBe("sent-put");
    }

    [Fact]
    public async Task put_surfaces_a_non_200_status_instead_of_throwing()
    {
        // Alba's default Scenario() asserts 200. The helpers call IgnoreStatusCode() so a spec
        // can assert on 409/404 paths deliberately — see docs/sample-wiring.md footgun 6.
        var result = await _context.PutJsonAsync<Thing, Thing>("/conflict", new Thing("x", 0));

        result.StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task delete_returns_the_status()
    {
        var result = await _context.DeleteAsync("/thing");

        result.StatusCode.ShouldBe(204);
    }

    public record Thing(string Name, int Count);
}
