using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class DiScopingTests
{
    private static BobcatRunner buildRunner()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(Di_Scoping_Feature.Define());
        runner.Suite.AddResource(new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddScoped<ISessionMarker, SessionMarker>();
            builder.Services.AddSingleton<IAppMarker, AppMarker>();
            return builder.Build();
        }));

        return runner;
    }

    [Fact]
    public async Task scenario_scope_drives_service_lifetimes_end_to_end()
    {
        DiScopingFixture.Reset();

        var results = await buildRunner().RunAll();

        var scenarios = results.Features.Single().Scenarios;
        foreach (var scenario in scenarios)
        {
            var failed = scenario.Results.Steps
                .Where(s => s.StepStatus == ResultStatus.failed || s.StepStatus == ResultStatus.error)
                .Select(s => $"{scenario.Title} / {s.StepText}")
                .ToList();

            failed.ShouldBeEmpty();
        }

        results.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task CurrentServices_throws_when_no_scenario_scope_is_open()
    {
        await using var resource = new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddScoped<ISessionMarker, SessionMarker>();
            return builder.Build();
        });

        await resource.Start();

        Should.Throw<InvalidOperationException>(() => resource.CurrentServices)
            .Message.ShouldContain("No scenario scope is open");
    }

    [Fact]
    public async Task scoped_instance_is_stable_within_a_scope_and_fresh_across_scopes()
    {
        await using var resource = new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddScoped<ISessionMarker, SessionMarker>();
            return builder.Build();
        });

        await resource.Start();

        await resource.BeginScenarioScope();
        var first = resource.CurrentServices.GetRequiredService<ISessionMarker>().Id;
        var firstAgain = resource.CurrentServices.GetRequiredService<ISessionMarker>().Id;
        await resource.EndScenarioScope();

        await resource.BeginScenarioScope();
        var second = resource.CurrentServices.GetRequiredService<ISessionMarker>().Id;
        await resource.EndScenarioScope();

        firstAgain.ShouldBe(first);
        second.ShouldNotBe(first);
    }

    [Fact]
    public async Task RootServices_is_the_hosts_root_container()
    {
        await using var resource = new HostResource(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton<IAppMarker, AppMarker>();
            return builder.Build();
        });

        await resource.Start();

        resource.RootServices.ShouldBeSameAs(resource.Host.Services);
    }
}
