using Alba;
using Bobcat.Alba.Tests.TestApp;
using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Alba.Tests;

public class AlbaExtensionTests : IAsyncLifetime
{
    private TestSuite _suite = null!;
    private SpecExecutionContext _context = null!;

    public async Task InitializeAsync()
    {
        _suite = new TestSuite();
        _suite.AddResource(TestWebApp.Create());
        await _suite.StartAll();
        _context = new SpecExecutionContext("test-spec", suite: _suite);
    }

    public async Task DisposeAsync()
    {
        await _suite.DisposeAsync();
    }

    [Fact]
    public void can_get_alba_resource()
    {
        var resource = _context.GetAlbaResource(TestWebApp.ResourceName);
        resource.ShouldNotBeNull();
        resource.Host.ShouldNotBeNull();
    }

    [Fact]
    public void can_get_alba_host()
    {
        var host = _context.GetAlbaHost(TestWebApp.ResourceName);
        host.ShouldNotBeNull();
    }

    [Fact]
    public async Task scenario_async_stores_last_result()
    {
        var result = await _context.ScenarioAsync(s =>
        {
            s.Get.Url("/api/hello");
            s.StatusCodeShouldBeOk();
        }, TestWebApp.ResourceName);

        result.ShouldNotBeNull();
        _context.LastScenarioResult(TestWebApp.ResourceName).ShouldBe(result);
    }

    [Fact]
    public async Task get_json_async_deserializes()
    {
        var response = await _context.GetJsonAsync<HelloResponse>("/api/hello", TestWebApp.ResourceName);
        response.Message.ShouldBe("hello");
    }

    [Fact]
    public async Task post_json_async_sends_body()
    {
        var result = await _context.PostJsonAsync<EchoRequest>(
            "/api/echo", new EchoRequest("test message"), TestWebApp.ResourceName);
        result.ShouldNotBeNull();

        var response = await result.ReadAsJsonAsync<EchoResponse>();
        response.Message.ShouldBe("test message");
    }

    [Fact]
    public async Task post_json_with_response_deserializes()
    {
        var response = await _context.PostJsonAsync<EchoRequest, EchoResponse>(
            "/api/echo", new EchoRequest("round trip"), TestWebApp.ResourceName);
        response.Message.ShouldBe("round trip");
    }

    [Fact]
    public async Task delete_async_works()
    {
        var result = await _context.ScenarioAsync(s =>
        {
            s.Delete.Url("/api/items/123");
            s.StatusCodeShouldBe(204);
        }, TestWebApp.ResourceName);
        result.Context.Response.StatusCode.ShouldBe(204);
    }

    [Fact]
    public async Task last_scenario_result_returns_null_before_any_scenario()
    {
        var resource = _context.GetAlbaResource(TestWebApp.ResourceName);
        await resource.ResetBetweenScenarios();
        _context.LastScenarioResult(TestWebApp.ResourceName).ShouldBeNull();
    }
}
