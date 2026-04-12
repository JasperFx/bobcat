using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Wolverine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace Bobcat.Wolverine.Tests;

public class WolverineExtensionsTests
{
    [Fact]
    public async Task InvokeMessageAndWaitAsync_delegates_to_host()
    {
        await using var resource = BuildWolverineHostResource();
        await resource.Start();

        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);

        var session = await context.InvokeMessageAndWaitAsync(new PingMessage("hello"));

        session.ShouldNotBeNull();
        session.Status.ShouldBe(TrackingStatus.Completed);
    }

    [Fact]
    public async Task SendMessageAndWaitAsync_delegates_to_host()
    {
        await using var resource = BuildWolverineHostResource();
        await resource.Start();

        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);

        var session = await context.SendMessageAndWaitAsync(new PingMessage("send-test"));

        session.ShouldNotBeNull();
        session.Status.ShouldBe(TrackingStatus.Completed);
    }

    [Fact]
    public async Task TrackActivity_returns_session_configuration()
    {
        await using var resource = BuildWolverineHostResource();
        await resource.Start();

        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);

        var config = context.TrackActivity();
        config.ShouldNotBeNull();
    }

    [Fact]
    public async Task can_use_named_resource()
    {
        await using var resource = BuildWolverineHostResource(name: "WolverineApp");
        await resource.Start();

        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>("WolverineApp").Returns(resource);

        var session = await context.InvokeMessageAndWaitAsync(
            new PingMessage("named"), resourceName: "WolverineApp");

        session.ShouldNotBeNull();
    }

    private static HostResource BuildWolverineHostResource(string? name = null)
    {
        return new HostResource(
            hostFactory: () =>
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Services.AddWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType<PingHandler>();
                });
                return Task.FromResult(builder.Build());
            },
            name: name);
    }
}

public record PingMessage(string Value);

public class PingHandler
{
    public void Handle(PingMessage message)
    {
        // No-op handler for testing
    }
}
