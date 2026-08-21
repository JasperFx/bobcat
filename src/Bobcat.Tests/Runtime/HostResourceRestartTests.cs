using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// <see cref="IRestartableResource"/> on the in-process hosts: a restart is a new container over
/// the same resource registration, and the scenario scope follows it.
/// </summary>
public class HostResourceRestartTests
{
    private static IHost buildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<Marker>().AddScoped<ScopedMarker>();
        return builder.Build();
    }

    [Fact]
    public void host_resources_are_restartable()
    {
        new HostResource(buildHost).ShouldBeAssignableTo<IRestartableResource>();
        new HostResource<HostResourceRestartTests>().ShouldBeAssignableTo<IRestartableResource>();
    }

    [Fact]
    public async Task restart_before_start_is_an_error()
    {
        await using var resource = new HostResource(buildHost);
        await Should.ThrowAsync<InvalidOperationException>(() => resource.Restart());
    }

    [Fact]
    public async Task restart_replaces_the_host_with_a_fresh_container()
    {
        await using var resource = new HostResource(buildHost);
        await resource.Start();

        var before = resource.Host;
        var beforeMarker = resource.RootServices.GetRequiredService<Marker>();

        await resource.Restart();

        resource.Host.ShouldNotBeSameAs(before);
        resource.RootServices.GetRequiredService<Marker>().ShouldNotBeSameAs(beforeMarker);
        resource.Host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.IsCancellationRequested.ShouldBeTrue("the new host should be started");
    }

    [Fact]
    public async Task restart_stops_and_disposes_the_old_host()
    {
        await using var resource = new HostResource(buildHost);
        await resource.Start();

        var old = resource.Host;
        var stopped = old.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped;

        await resource.Restart();

        stopped.IsCancellationRequested.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => old.Services.GetRequiredService<Marker>());
    }

    [Fact]
    public async Task restart_inside_a_scenario_reopens_the_scope_on_the_new_host()
    {
        await using var resource = new HostResource(buildHost);
        await resource.Start();
        await resource.BeginScenarioScope();

        var scopedBefore = resource.CurrentServices.GetRequiredService<ScopedMarker>();

        await resource.Restart();

        // Still inside the scenario: CurrentServices resolves, and from the NEW container.
        var scopedAfter = resource.CurrentServices.GetRequiredService<ScopedMarker>();
        scopedAfter.ShouldNotBeSameAs(scopedBefore);
        scopedAfter.Root.ShouldBeSameAs(resource.RootServices.GetRequiredService<Marker>());

        await resource.EndScenarioScope();
    }

    [Fact]
    public async Task restart_outside_a_scenario_leaves_no_scope_open()
    {
        await using var resource = new HostResource(buildHost);
        await resource.Start();

        await resource.Restart();

        Should.Throw<InvalidOperationException>(() => resource.CurrentServices);
    }

    [Fact]
    public async Task restart_does_not_run_the_reset_hook()
    {
        var resets = 0;
        await using var resource = new HostResource(buildHost, reset: _ => { resets++; return Task.CompletedTask; });
        await resource.Start();

        await resource.Restart();

        resets.ShouldBe(0);
    }

    [Fact]
    public async Task generic_host_resource_rebuilds_through_the_same_configure_callback()
    {
        var builds = 0;
        await using var resource = new HostResource<HostResourceRestartTests>(builder =>
        {
            builds++;
            builder.Services.AddSingleton<Marker>();
        });
        await resource.Start();
        var before = resource.RootServices.GetRequiredService<Marker>();

        await resource.Restart();

        builds.ShouldBe(2);
        resource.RootServices.GetRequiredService<Marker>().ShouldNotBeSameAs(before);
    }

    [Fact]
    public async Task RestartHost_extension_restarts_the_named_host_resource()
    {
        var suite = new TestSuite();
        var resource = new HostResource(buildHost, name: "app");
        suite.AddResource(resource);
        await suite.StartAll();
        var context = new SpecExecutionContext("spec", suite: suite);
        var before = resource.Host;

        await context.RestartHost("app");

        resource.Host.ShouldNotBeSameAs(before);
        await suite.DisposeAsync();
    }

    [Fact]
    public async Task RestartHost_extension_names_a_host_that_cannot_restart()
    {
        var suite = new TestSuite();
        suite.AddResource(new FixedHostResource("fixed"));
        var context = new SpecExecutionContext("spec", suite: suite);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => context.RestartHost("fixed"));
        ex.Message.ShouldContain("fixed");
        ex.Message.ShouldContain(nameof(IRestartableResource));
    }

    private sealed class Marker;

    private sealed class ScopedMarker(Marker root)
    {
        public Marker Root { get; } = root;
    }

    /// <summary>An IHostResource that is deliberately not restartable.</summary>
    private sealed class FixedHostResource(string name) : IHostResource
    {
        public string Name { get; } = name;
        public IHost Host => throw new NotSupportedException();
        public IServiceProvider RootServices => throw new NotSupportedException();
        public IServiceProvider CurrentServices => throw new NotSupportedException();
        public Task Start() => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public ValueTask BeginScenarioScope() => ValueTask.CompletedTask;
        public ValueTask EndScenarioScope() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
