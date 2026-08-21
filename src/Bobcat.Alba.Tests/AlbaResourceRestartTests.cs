using Alba;
using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Bobcat.Alba.Tests;

/// <summary>
/// <see cref="IRestartableResource"/> on the Alba hosts — the verb the test-run viewer's
/// "a restart forgets nothing" scenarios needed and had to hand-roll (issue #62 gap 4).
/// </summary>
public class AlbaResourceRestartTests
{
    private static Task<IAlbaHost> buildTestAlbaHost()
        => AlbaHost.For(WebApplication.CreateBuilder(), app =>
        {
            app.MapGet("/hello", () => "Hello");
        });

    [Fact]
    public void alba_resources_are_restartable()
    {
        new AlbaResource(buildTestAlbaHost).ShouldBeAssignableTo<IRestartableResource>();
        new AlbaResource<SampleWeb.Program>().ShouldBeAssignableTo<IRestartableResource>();
    }

    [Fact]
    public async Task restart_before_start_is_an_error()
    {
        await using var factoryBased = new AlbaResource(buildTestAlbaHost);
        await Should.ThrowAsync<InvalidOperationException>(() => factoryBased.Restart());

        await using var generic = new AlbaResource<SampleWeb.Program>();
        await Should.ThrowAsync<InvalidOperationException>(() => generic.Restart());
    }

    [Fact]
    public async Task factory_resource_restart_builds_a_new_host_and_disposes_the_old_one()
    {
        await using var resource = new AlbaResource(buildTestAlbaHost);
        await resource.Start();
        var old = resource.AlbaHost;

        await resource.Restart();

        resource.AlbaHost.ShouldNotBeSameAs(old);
        Should.Throw<ObjectDisposedException>(() => old.Services.GetRequiredService<IServiceScopeFactory>());

        var result = await resource.AlbaHost.Scenario(s => s.Get.Url("/hello"));
        (await result.ReadAsTextAsync()).ShouldBe("Hello");
    }

    [Fact]
    public async Task generic_resource_restart_is_a_fresh_application_over_the_same_registration()
    {
        await using var resource = new AlbaResource<SampleWeb.Program>();
        await resource.Start();

        // The singleton counter proves which application answered: the first host has counted
        // to 2, a fresh one starts over at 1.
        (await next(resource)).ShouldBe(1);
        (await next(resource)).ShouldBe(2);

        await resource.Restart();

        (await next(resource)).ShouldBe(1);
        resource.Name.ShouldBe("Program");
    }

    [Fact]
    public async Task generic_resource_restart_reapplies_the_configure_callback_and_content_root()
    {
        var configured = 0;
        await using var resource = new AlbaResource<SampleWeb.Program>(configure: _ => configured++)
            .WithContentRoot(AppContext.BaseDirectory);
        await resource.Start();

        await resource.Restart();

        configured.ShouldBe(2);
        var root = await resource.AlbaHost.Scenario(s => s.Get.Url("/content-root"));
        Path.TrimEndingDirectorySeparator(await root.ReadAsTextAsync())
            .ShouldBe(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    }

    [Fact]
    public async Task restart_inside_a_scenario_reopens_the_scope_on_the_new_container()
    {
        await using var resource = new AlbaResource<SampleWeb.Program>();
        await resource.Start();
        await resource.BeginScenarioScope();

        var factoryBefore = resource.CurrentServices.GetRequiredService<IServiceScopeFactory>();

        await resource.Restart();

        var factoryAfter = resource.CurrentServices.GetRequiredService<IServiceScopeFactory>();
        factoryAfter.ShouldNotBeSameAs(factoryBefore);
        factoryAfter.ShouldBeSameAs(resource.RootServices.GetRequiredService<IServiceScopeFactory>());

        await resource.EndScenarioScope();
        Should.Throw<InvalidOperationException>(() => resource.CurrentServices);
    }

    [Fact]
    public async Task restart_outside_a_scenario_leaves_no_scope_open()
    {
        await using var resource = new AlbaResource(buildTestAlbaHost);
        await resource.Start();

        await resource.Restart();

        Should.Throw<InvalidOperationException>(() => resource.CurrentServices);
    }

    [Fact]
    public async Task restart_does_not_run_the_reset_hook()
    {
        var resets = 0;
        await using var resource = new AlbaResource(buildTestAlbaHost, reset: _ => { resets++; return Task.CompletedTask; });
        await resource.Start();

        await resource.Restart();

        resets.ShouldBe(0);
    }

    [Fact]
    public async Task RestartHost_from_a_step_context_restarts_the_registered_alba_host()
    {
        var suite = new TestSuite();
        var resource = new AlbaResource<SampleWeb.Program>(name: "web");
        suite.AddResource(resource);
        await suite.StartAll();
        var context = new SpecExecutionContext("spec", suite: suite);

        (await next(resource)).ShouldBe(1);
        await context.RestartHost("web");
        (await next(resource)).ShouldBe(1);

        // Still the same registration: the helpers find it by name after the restart.
        (await context.GetJsonAsync<int>("/counter/next", "web")).Body.ShouldBe(2);

        await suite.DisposeAsync();
    }

    private static async Task<int> next(IAlbaResource resource)
    {
        var result = await resource.AlbaHost.Scenario(s => s.Get.Url("/counter/next"));
        return int.Parse(await result.ReadAsTextAsync());
    }
}
