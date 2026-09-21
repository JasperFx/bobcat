using Bobcat.Alba;
using Bobcat.Alba.SampleWeb;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Bobcat.Alba.Tests;

/// <summary>
/// <see cref="AlbaResource{TProgram}"/> against a real ASP.NET Core host, booted the way a
/// consumer boots one.
/// </summary>
public class AlbaResourceTests
{
    [Fact]
    public async Task starts_a_host_that_answers()
    {
        await using var resource = new AlbaResource<Program>();
        await resource.StartAsync(TestContext.Current.CancellationToken);

        var result = await resource.AlbaHost.Scenario(s => s.Get.Url("/hello"));
        result.ReadAsText().ShouldBe("Hello from SampleWeb");
    }

    [Fact]
    public async Task the_host_is_not_available_before_it_starts()
    {
        await using var resource = new AlbaResource<Program>();
        Should.Throw<InvalidOperationException>(() => resource.AlbaHost)
            .Message.ShouldContain("has not been started");
    }

    /// <summary>
    /// The capability the hand-written sample copies silently lost: a resource wrapping an IHost
    /// must BE an <see cref="IHostResource"/>, or every IStepContext helper that resolves a host
    /// fails to find it and no per-scenario DI scope is ever opened.
    /// </summary>
    [Fact]
    public async Task is_a_host_resource_so_the_step_context_helpers_can_find_it()
    {
        await using var resource = new AlbaResource<Program>();
        await resource.StartAsync(TestContext.Current.CancellationToken);

        var resources = new TestResources();
        resources.Add(resource);

        resources.GetResource<IHostResource>().ShouldBeSameAs(resource);
        resource.RootServices.GetRequiredService<Counter>().ShouldNotBeNull();
    }

    [Fact]
    public async Task opens_and_closes_a_per_scenario_scope()
    {
        await using var resource = new AlbaResource<Program>();
        await resource.StartAsync(TestContext.Current.CancellationToken);

        Should.Throw<InvalidOperationException>(() => resource.CurrentServices)
            .Message.ShouldContain("No scenario scope is open");

        await resource.BeginScenarioScope();
        resource.CurrentServices.ShouldNotBeNull();

        await resource.EndScenarioScope();
        Should.Throw<InvalidOperationException>(() => resource.CurrentServices);
    }

    [Fact]
    public async Task the_content_root_resolves_to_the_web_project_not_the_solution_guess()
    {
        await using var resource = new AlbaResource<Program>();
        await resource.StartAsync(TestContext.Current.CancellationToken);

        var root = (await resource.AlbaHost.Scenario(s => s.Get.Url("/content-root"))).ReadAsText();

        Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).ShouldBe("Bobcat.Alba.SampleWeb");
    }

    [Fact]
    public async Task the_reset_hook_runs_between_scenarios_and_is_optional()
    {
        var resets = 0;

        await using var withHook = new AlbaResource<Program>(reset: _ => { resets++; return Task.CompletedTask; });
        await withHook.StartAsync(TestContext.Current.CancellationToken);
        await withHook.ResetBetweenScenarios();
        await withHook.ResetBetweenScenarios();
        resets.ShouldBe(2);

        await using var withNone = new AlbaResource<Program>();
        await withNone.StartAsync(TestContext.Current.CancellationToken);
        await Should.NotThrowAsync(() => withNone.ResetBetweenScenarios());
    }

    [Fact]
    public async Task stopping_releases_the_host_and_is_idempotent()
    {
        var resource = new AlbaResource<Program>();
        await resource.StartAsync(TestContext.Current.CancellationToken);

        await resource.StopAsync(TestContext.Current.CancellationToken);
        Should.Throw<InvalidOperationException>(() => resource.AlbaHost);

        await Should.NotThrowAsync(() => resource.StopAsync(TestContext.Current.CancellationToken));
        await Should.NotThrowAsync(async () => await resource.DisposeAsync());
    }

    [Fact]
    public async Task two_resources_can_be_named_apart()
    {
        await using var a = new AlbaResource<Program>(name: "orders");
        await using var b = new AlbaResource<Program>(name: "billing");

        var resources = new TestResources();
        resources.Add(a);
        resources.Add(b);

        resources.GetResource<IAlbaResource>("orders").ShouldBeSameAs(a);
        resources.GetResource<IAlbaResource>("billing").ShouldBeSameAs(b);
    }
}
