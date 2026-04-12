using Alba;
using Bobcat.Engine;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Bobcat.Alba.Tests;

/// <summary>
/// Integration tests for AlbaStepContextExtensions using the minimal web app
/// defined in Program.cs of this test project.
/// </summary>
public class AlbaStepContextExtensionTests : IAsyncLifetime
{
    private IAlbaHost _host = null!;
    private AlbaResource<Program> _resource = null!;
    private IStepContext _context = null!;

    public async Task InitializeAsync()
    {
        _host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
        });
        _resource = new AlbaResource<Program>("test-app", _host);

        _context = Substitute.For<IStepContext>();
        _context.GetResource<AlbaResource<Program>>(null).Returns(_resource);
        _context.GetResource<AlbaResource<Program>>(Arg.Any<string?>()).Returns(_resource);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task scenario_async_executes_and_stores_last_result()
    {
        var result = await _context.ScenarioAsync<Program>(s =>
        {
            s.Get.Url("/api/hello");
        });

        result.ShouldNotBeNull();
        _resource.LastResult.ShouldBe(result);
    }

    [Fact]
    public async Task get_json_async_deserializes_correctly()
    {
        var response = await _context.GetJsonAsync<Program, HelloResponse>("/api/hello");

        response.ShouldNotBeNull();
        response.Message.ShouldBe("hello");
    }

    [Fact]
    public async Task post_json_async_sends_body_and_returns_result()
    {
        var payload = new HelloResponse("test-payload");
        var result = await _context.PostJsonAsync<Program, HelloResponse>(
            "/api/echo", payload);

        result.ShouldNotBeNull();
        result.Context.Response.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task delete_async_works()
    {
        var result = await _context.ScenarioAsync<Program>(s =>
        {
            s.Delete.Url("/api/items/42");
            s.StatusCodeShouldBe(204);
        });

        result.Context.Response.StatusCode.ShouldBe(204);
    }

    [Fact]
    public async Task last_scenario_result_returns_stored_result()
    {
        _resource.LastResult.ShouldBeNull();

        await _context.ScenarioAsync<Program>(s => s.Get.Url("/api/hello"));

        var last = _context.LastScenarioResult<Program>();
        last.ShouldNotBeNull();
        last.ShouldBe(_resource.LastResult);
    }

    [Fact]
    public async Task get_alba_resource_returns_the_resource()
    {
        var resource = _context.GetAlbaResource<Program>();
        resource.ShouldBe(_resource);
    }

    [Fact]
    public async Task get_alba_host_returns_the_host()
    {
        var host = _context.GetAlbaHost<Program>();
        host.ShouldBe(_host);
    }
}
