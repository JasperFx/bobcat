using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Wolverine;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;

namespace Bobcat.Wolverine.Tests;

public record WarmOne;
public record WarmTwo;
public record WarmThree;

public class WarmUpHandlers
{
    public static void Handle(WarmOne _) { }
    public static void Handle(WarmTwo _) { }
    public static void Handle(WarmThree _) { }
}

public interface IUnregisteredDependency;

public record BrokenMessage;

/// <summary>
/// Cannot compile once service location is refused: nothing in the container can supply its
/// dependency.
/// </summary>
public class BrokenHandler
{
    public static void Handle(BrokenMessage _, IUnregisteredDependency dependency) { }
}

/// <summary>
/// <see cref="HandlerWarmUp.Automatic"/> is process-wide, so the tests that depend on (or flip) it
/// run on their own.
/// </summary>
[CollectionDefinition(nameof(HandlerWarmUpCollection), DisableParallelization = true)]
public class HandlerWarmUpCollection;

/// <summary>
/// Issue #287: Wolverine compiles a handler on first use, and on a cold host that compile landed
/// inside the first tracked act's timeout window.
/// </summary>
[Collection(nameof(HandlerWarmUpCollection))]
public class HandlerWarmUpTests
{
    [Fact]
    public async Task warming_compiles_every_handler_chain_once()
    {
        await using var resource = hostResource(typeof(WarmUpHandlers), typeof(PingHandler));
        await resource.Start();

        var report = resource.Host.WarmUpHandlers();

        report.AllHandlers.ShouldBeTrue();
        report.Warmed.ShouldBe(chainCount(resource.Host));
        report.Warmed.ShouldBeGreaterThanOrEqualTo(4);
        report.SkippedStickyOnly.ShouldBeEmpty();

        // Once per host: a second ask compiles nothing and says so by being the same report.
        resource.Host.WarmUpHandlers().ShouldBeSameAs(report);
    }

    [Fact]
    public async Task the_first_tracked_act_primes_the_compiler_before_its_session_starts()
    {
        await using var resource = hostResource(typeof(WarmUpHandlers), typeof(PingHandler));
        await resource.Start();
        var context = contextFor(resource);

        HandlerWarmUp.ReportFor(resource.Host).ShouldBeNull();

        await context.InvokeMessageAndWaitAsync(new PingMessage("first"));

        // The default: one chain, which is what starts the compiler — not every handler the
        // application has.
        var primed = HandlerWarmUp.ReportFor(resource.Host).ShouldNotBeNull();
        primed.AllHandlers.ShouldBeFalse();
        primed.Warmed.ShouldBe(1);

        // And only once: the next act finds the host warm.
        await context.InvokeMessageAndWaitAsync(new PingMessage("second"));
        HandlerWarmUp.ReportFor(resource.Host).ShouldBeSameAs(primed);
    }

    [Fact]
    public async Task asking_a_primed_host_for_everything_compiles_the_rest()
    {
        await using var resource = hostResource(typeof(WarmUpHandlers), typeof(PingHandler));
        await resource.Start();

        contextFor(resource).TrackActivity();
        HandlerWarmUp.ReportFor(resource.Host)!.AllHandlers.ShouldBeFalse();

        var report = resource.Host.WarmUpHandlers();

        report.AllHandlers.ShouldBeTrue();
        report.Warmed.ShouldBe(chainCount(resource.Host));
    }

    [Fact]
    public async Task off_leaves_the_host_cold()
    {
        await using var resource = hostResource(typeof(WarmUpHandlers), typeof(PingHandler));
        await resource.Start();

        HandlerWarmUp.Automatic = AutomaticWarmUp.Off;
        try
        {
            await contextFor(resource).InvokeMessageAndWaitAsync(new PingMessage("cold"));
            HandlerWarmUp.ReportFor(resource.Host).ShouldBeNull();
        }
        finally
        {
            HandlerWarmUp.Automatic = AutomaticWarmUp.PrimeCompiler;
        }
    }

    [Fact]
    public async Task the_global_action_compiles_everything_before_the_first_feature()
    {
        await using var resource = hostResource(typeof(WarmUpHandlers), typeof(PingHandler));
        await resource.Start();

        var action = new WarmUpWolverineHandlers(resource);
        await action.SetUp();

        action.Report.ShouldNotBeNull().AllHandlers.ShouldBeTrue();
        action.Report.Warmed.ShouldBe(chainCount(resource.Host));
    }

    [Fact]
    public async Task a_handler_that_cannot_compile_is_reported_by_name_not_swallowed()
    {
        await using var resource = hostResource(
            opts => opts.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed,
            typeof(WarmUpHandlers), typeof(BrokenHandler));
        await resource.Start();

        var failure = Should.Throw<HandlerWarmUpException>(() => resource.Host.WarmUpHandlers());

        failure.Failures.Select(f => f.MessageType).ShouldBe([typeof(BrokenMessage)]);
        failure.Message.ShouldContain(typeof(BrokenMessage).FullName!);

        // Remembered: a later ask rethrows rather than compiling the broken chain again.
        Should.Throw<HandlerWarmUpException>(() => resource.Host.WarmUpHandlers()).ShouldBeSameAs(failure);
        HandlerWarmUp.ReportFor(resource.Host).ShouldBeNull();
    }

    private static int chainCount(IHost host)
        => ((WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>()).Handlers.Chains.Length;

    private static IStepContext contextFor(IHostResource resource)
    {
        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);
        return context;
    }

    private static HostResource hostResource(params Type[] handlers)
        => hostResource(_ => { }, handlers);

    private static HostResource hostResource(Action<WolverineOptions> configure, params Type[] handlers)
        => new(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handler in handlers) discovery.IncludeType(handler);
                configure(opts);
            });
            return Task.FromResult(builder.Build());
        });
}
