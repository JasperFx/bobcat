using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;

namespace Bobcat.Wolverine.Tests;

public class HostResourceTests
{
    [Fact]
    public void implements_IHostResource()
    {
        var resource = new HostResource(() => Host.CreateApplicationBuilder().Build());
        resource.ShouldBeAssignableTo<IHostResource>();
    }

    [Fact]
    public void default_name_is_Host()
    {
        var resource = new HostResource(() => Host.CreateApplicationBuilder().Build());
        resource.Name.ShouldBe("Host");
    }

    [Fact]
    public void custom_name_is_used()
    {
        var resource = new HostResource(() => Host.CreateApplicationBuilder().Build(), name: "MyApp");
        resource.Name.ShouldBe("MyApp");
    }

    [Fact]
    public async Task Start_builds_and_starts_host()
    {
        await using var resource = new HostResource(
            () => Host.CreateApplicationBuilder().Build());

        await resource.Start();

        resource.Host.ShouldNotBeNull();
    }

    [Fact]
    public async Task DisposeAsync_stops_host()
    {
        var resource = new HostResource(() => Host.CreateApplicationBuilder().Build());
        await resource.Start();

        await resource.DisposeAsync();

        // Should not throw — host disposed cleanly
    }

    [Fact]
    public async Task ResetBetweenScenarios_calls_reset_delegate()
    {
        var resetCalled = false;

        await using var resource = new HostResource(
            () => Host.CreateApplicationBuilder().Build(),
            reset: _ => { resetCalled = true; return Task.CompletedTask; });

        await resource.Start();
        await resource.ResetBetweenScenarios();

        resetCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task can_resolve_service_from_host()
    {
        await using var resource = new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<ISampleService, SampleService>();
            return Task.FromResult(builder.Build());
        });

        await resource.Start();

        var svc = resource.Host.Services.GetRequiredService<ISampleService>();
        svc.ShouldNotBeNull();
        svc.ShouldBeOfType<SampleService>();
    }

    [Fact]
    public async Task generic_HostResource_uses_TProgram_as_name()
    {
        var resource = new HostResource<SampleApp>();
        resource.Name.ShouldBe("SampleApp");
    }

    [Fact]
    public async Task generic_HostResource_builds_and_starts()
    {
        await using var resource = new HostResource<SampleApp>(configure: builder =>
        {
            builder.Services.AddSingleton<ISampleService, SampleService>();
        });

        await resource.Start();

        resource.Host.ShouldNotBeNull();
        var svc = resource.Host.Services.GetRequiredService<ISampleService>();
        svc.ShouldNotBeNull();
    }
}

public interface ISampleService { }
public class SampleService : ISampleService { }
public class SampleApp { }
