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

            // Representations a typed helper cannot see: the export shapes from #62 gap 5.
            app.MapGet("/export/xml", () => Results.Content("<testsuites tests=\"2\" />", "application/xml"));
            app.MapGet("/export/ndjson", () => Results.Text("{\"n\":1}\n{\"n\":2}\n", "application/x-ndjson"));
            app.MapGet("/export/json", () => Results.Json(new Thing("exported", 7)));
            app.MapGet("/export/missing", () => Results.Problem("no such format", statusCode: 400));
            app.MapGet("/export/headers", (HttpResponse response) =>
            {
                response.Headers["X-Export-Count"] = "3";
                response.Headers.Append("X-Multi", "a");
                response.Headers.Append("X-Multi", "b");
                return Results.Text("ok");
            });
            app.MapGet("/export/empty", () => Results.NoContent());
            app.MapPost("/echo", async (HttpRequest request) =>
            {
                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync();
                return Results.Text($"{request.ContentType}|{body}", "text/plain");
            });
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

    // --- raw responses (#62 gap 5): status + content type + headers + body, no deserialization ---

    [Fact]
    public async Task get_raw_returns_status_content_type_and_body_for_xml()
    {
        var raw = await _context.GetRawAsync("/export/xml");

        raw.StatusCode.ShouldBe(200);
        raw.MediaType.ShouldBe("application/xml");
        raw.Body.ShouldBe("<testsuites tests=\"2\" />");
    }

    [Fact]
    public async Task get_raw_keeps_an_ndjson_body_verbatim()
    {
        var raw = await _context.GetRawAsync("/export/ndjson");

        raw.MediaType.ShouldBe("application/x-ndjson");
        raw.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
        raw.Bytes.Length.ShouldBe(raw.Body.Length);
    }

    [Fact]
    public async Task get_raw_separates_media_type_from_the_full_content_type()
    {
        var raw = await _context.GetRawAsync("/export/json");

        raw.ContentType.ShouldBe("application/json; charset=utf-8");
        raw.MediaType.ShouldBe("application/json");
        raw.ReadAsJson<Thing>()!.Name.ShouldBe("exported");
    }

    [Fact]
    public async Task get_raw_surfaces_a_non_200_status_instead_of_throwing()
    {
        var raw = await _context.GetRawAsync("/export/missing");

        raw.StatusCode.ShouldBe(400);
        raw.MediaType.ShouldBe("application/problem+json");
        raw.Body.ShouldContain("no such format");
    }

    [Fact]
    public async Task get_raw_exposes_every_response_header_case_insensitively()
    {
        var raw = await _context.GetRawAsync("/export/headers");

        raw.Headers["x-export-count"].ShouldBe(["3"]);
        raw.Headers["X-Multi"].ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task get_raw_of_an_empty_body_is_an_empty_string_not_a_throw()
    {
        var raw = await _context.GetRawAsync("/export/empty");

        raw.StatusCode.ShouldBe(204);
        raw.Body.ShouldBe("");
        raw.Bytes.ShouldBeEmpty();
        raw.ContentType.ShouldBeNull();
        raw.MediaType.ShouldBeNull();
    }

    [Fact]
    public async Task read_as_json_on_a_non_json_body_throws_rather_than_returning_default()
    {
        var raw = await _context.GetRawAsync("/export/xml");

        Should.Throw<System.Text.Json.JsonException>(() => raw.ReadAsJson<Thing>());
    }

    [Fact]
    public async Task post_raw_sends_the_body_with_the_given_content_type()
    {
        var raw = await _context.PostRawAsync("/echo", "{\"n\":1}\n{\"n\":2}\n", "application/x-ndjson");

        raw.StatusCode.ShouldBe(200);
        raw.Body.ShouldBe("application/x-ndjson|{\"n\":1}\n{\"n\":2}\n");
    }

    [Fact]
    public async Task send_raw_runs_an_arbitrary_scenario_without_alba_asserting_the_status()
    {
        var raw = await _context.SendRawAsync(s =>
        {
            s.Get.Url("/export/missing");
            s.WithRequestHeader("Accept", "application/json");
        });

        raw.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task raw_helpers_find_the_host_by_resource_name()
    {
        var raw = await _context.GetRawAsync("/export/xml", "AlbaHost");

        raw.StatusCode.ShouldBe(200);
    }

    public record Thing(string Name, int Count);
}
