using Alba;
using Bobcat.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.Alba.Tests;

public class AlbaResourceTests
{
    // Factory that builds a minimal web app using WebApplicationBuilder (no TProgram entry point needed)
    private static Task<IAlbaHost> BuildTestAlbaHost()
        => AlbaHost.For(WebApplication.CreateBuilder(), app =>
        {
            app.MapGet("/hello", () => "Hello from TestApp!");
            app.MapGet("/ping", () => "pong");
        });

    // --- AlbaResource<TProgram> (non-started) tests ---

    [Fact]
    public void generic_implements_IHostResource()
    {
        var resource = new AlbaResource<TestApp>();
        resource.ShouldBeAssignableTo<IHostResource>();
    }

    [Fact]
    public void generic_default_name_is_TProgram_type_name()
    {
        var resource = new AlbaResource<TestApp>();
        resource.Name.ShouldBe("TestApp");
    }

    [Fact]
    public void generic_custom_name_is_used()
    {
        var resource = new AlbaResource<TestApp>(name: "Api");
        resource.Name.ShouldBe("Api");
    }

    [Fact]
    public void generic_Host_throws_before_Start()
    {
        var resource = new AlbaResource<TestApp>();
        Should.Throw<InvalidOperationException>(() => _ = resource.Host);
    }

    // --- AlbaResource (non-generic, factory-based) lifecycle tests ---

    [Fact]
    public void factory_implements_IHostResource()
    {
        var resource = new AlbaResource(BuildTestAlbaHost);
        resource.ShouldBeAssignableTo<IHostResource>();
    }

    [Fact]
    public void factory_default_name_is_AlbaHost()
    {
        var resource = new AlbaResource(BuildTestAlbaHost);
        resource.Name.ShouldBe("AlbaHost");
    }

    [Fact]
    public void factory_custom_name_is_used()
    {
        var resource = new AlbaResource(BuildTestAlbaHost, name: "Api");
        resource.Name.ShouldBe("Api");
    }

    [Fact]
    public void factory_Host_throws_before_Start()
    {
        var resource = new AlbaResource(BuildTestAlbaHost);
        Should.Throw<InvalidOperationException>(() => _ = resource.Host);
    }

    [Fact]
    public async Task Start_creates_running_host()
    {
        await using var resource = new AlbaResource(BuildTestAlbaHost);

        await resource.Start();

        resource.Host.ShouldNotBeNull();
        resource.AlbaHost.ShouldNotBeNull();
    }

    [Fact]
    public async Task Host_is_IAlbaHost()
    {
        await using var resource = new AlbaResource(BuildTestAlbaHost);

        await resource.Start();

        // IAlbaHost : IHost, so Host returns the IAlbaHost directly
        resource.Host.ShouldBeSameAs(resource.AlbaHost);
    }

    [Fact]
    public async Task can_execute_http_scenario()
    {
        await using var resource = new AlbaResource(BuildTestAlbaHost);

        await resource.Start();

        var result = await resource.AlbaHost.Scenario(s =>
        {
            s.Get.Url("/hello");
            s.StatusCodeShouldBeOk();
        });

        var body = await result.ReadAsTextAsync();
        body.ShouldBe("Hello from TestApp!");
    }

    [Fact]
    public async Task ResetBetweenScenarios_with_no_reset_does_nothing()
    {
        await using var resource = new AlbaResource(BuildTestAlbaHost);
        await resource.Start();

        await Should.NotThrowAsync(() => resource.ResetBetweenScenarios());
    }

    [Fact]
    public async Task ResetBetweenScenarios_calls_reset_delegate()
    {
        var resetCalled = false;

        await using var resource = new AlbaResource(
            BuildTestAlbaHost,
            reset: _ => { resetCalled = true; return Task.CompletedTask; });

        await resource.Start();
        await resource.ResetBetweenScenarios();

        resetCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task can_resolve_services_via_IHostResource()
    {
        await using var resource = new AlbaResource(BuildTestAlbaHost);
        await resource.Start();

        var services = resource.Host.Services;
        services.ShouldNotBeNull();
    }
}
