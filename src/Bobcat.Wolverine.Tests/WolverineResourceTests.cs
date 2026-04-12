using Bobcat.Wolverine.Tests.TestSupport;
using Shouldly;
using Wolverine.Tracking;

namespace Bobcat.Wolverine.Tests;

public class WolverineResourceTests
{
    [Fact]
    public void default_name_is_wolverine()
    {
        var resource = new WolverineResource(HostFactory.Create);
        resource.Name.ShouldBe("wolverine");
    }

    [Fact]
    public void custom_name_is_used()
    {
        var resource = new WolverineResource("my-app", HostFactory.Create);
        resource.Name.ShouldBe("my-app");
    }

    [Fact]
    public void host_throws_before_start()
    {
        var resource = new WolverineResource(HostFactory.Create);
        Should.Throw<InvalidOperationException>(() => _ = resource.Host);
    }

    [Fact]
    public async Task host_is_available_after_start()
    {
        await using var resource = new WolverineResource(HostFactory.Create);
        await resource.Start();
        resource.Host.ShouldNotBeNull();
    }

    [Fact]
    public async Task last_session_is_null_initially()
    {
        await using var resource = new WolverineResource(HostFactory.Create);
        await resource.Start();
        resource.LastSession.ShouldBeNull();
    }

    [Fact]
    public async Task reset_between_scenarios_clears_last_session()
    {
        await using var resource = new WolverineResource(
            HostFactory.Create,
            opts => opts.Discovery.IncludeAssembly(typeof(WolverineResourceTests).Assembly));
        await resource.Start();

        var session = await resource.Host.InvokeMessageAndWaitAsync(new PingMessage("hello"));
        resource.LastSession = session;
        resource.LastSession.ShouldNotBeNull();

        await resource.ResetBetweenScenarios();
        resource.LastSession.ShouldBeNull();
    }

    [Fact]
    public async Task implements_ITestResource()
    {
        var resource = new WolverineResource(HostFactory.Create);
        resource.ShouldBeAssignableTo<Bobcat.Runtime.ITestResource>();
        await resource.DisposeAsync();
    }
}
