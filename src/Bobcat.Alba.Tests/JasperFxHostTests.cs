using Alba;
using Bobcat.Runtime;
using JasperFx.CommandLine;
using Microsoft.AspNetCore.Builder;
using Shouldly;

namespace Bobcat.Alba.Tests;

/// <summary>
/// A host whose <c>Main</c> ends in <c>RunJasperFxCommands</c> — the shape of every Critter Stack
/// application — under <c>AlbaResource</c>. JasperFx's command runner needs
/// <c>JasperFxEnvironment.AutoStartHost</c> to cooperate with WebApplicationFactory, and nothing
/// in Bobcat set it (issue #62 gap 10). These run in one class so they are sequential: the flag
/// is process-wide.
/// </summary>
public class JasperFxHostTests
{
    [Fact]
    public async Task starting_a_generic_alba_resource_turns_auto_start_host_on()
    {
        JasperFxEnvironment.AutoStartHost = false;

        await using var resource = new AlbaResource<SampleJasperFxWeb.Program>();
        await resource.Start();

        JasperFxEnvironment.AutoStartHost.ShouldBeTrue();
    }

    [Fact]
    public async Task starting_a_factory_alba_resource_turns_auto_start_host_on()
    {
        JasperFxEnvironment.AutoStartHost = false;

        await using var resource = new AlbaResource(() => AlbaHost.For(WebApplication.CreateBuilder(), _ => { }));
        await resource.Start();

        JasperFxEnvironment.AutoStartHost.ShouldBeTrue();
    }

    [Fact]
    public void prepare_is_idempotent_and_never_turns_the_flag_off()
    {
        JasperFxEnvironment.AutoStartHost = true;
        AlbaResource.PrepareJasperFxHosting();
        JasperFxEnvironment.AutoStartHost.ShouldBeTrue();

        JasperFxEnvironment.AutoStartHost = false;
        AlbaResource.PrepareJasperFxHosting();
        AlbaResource.PrepareJasperFxHosting();
        JasperFxEnvironment.AutoStartHost.ShouldBeTrue();
    }

    [Fact]
    public async Task a_run_jasperfx_commands_host_starts_serves_and_is_started_exactly_once()
    {
        JasperFxEnvironment.AutoStartHost = false;

        await using var resource = new AlbaResource<SampleJasperFxWeb.Program>();
        await resource.Start();

        var hello = await resource.AlbaHost.Scenario(s => s.Get.Url("/hello"));
        (await hello.ReadAsTextAsync()).ShouldBe("Hello from SampleJasperFxWeb");

        // Under AutoStartHost JasperFx starts the already-built host itself and its run command
        // skips the redundant start, so the hosted services run StartAsync once. Sample twice with
        // a pause so the command runner, which is still parsing on the entry point's thread when
        // the first request lands, has had its turn.
        (await starts(resource)).ShouldBe(1);
        await Task.Delay(TimeSpan.FromSeconds(1));
        (await starts(resource)).ShouldBe(1);
    }

    private static async Task<int> starts(IAlbaResource resource)
    {
        var result = await resource.AlbaHost.Scenario(s => s.Get.Url("/starts"));
        return int.Parse(await result.ReadAsTextAsync());
    }
}
