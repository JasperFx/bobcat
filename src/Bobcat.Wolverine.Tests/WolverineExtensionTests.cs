using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Wolverine.Tests.TestSupport;
using Shouldly;
using Wolverine.Tracking;

namespace Bobcat.Wolverine.Tests;

public class WolverineExtensionTests : IAsyncLifetime
{
    private readonly WolverineResource _resource = new(
        HostFactory.Create,
        opts => opts.Discovery.IncludeAssembly(typeof(WolverineExtensionTests).Assembly));
    private readonly IStepContext _context;

    public WolverineExtensionTests()
    {
        var suite = new TestSuite();
        suite.AddResource(_resource);

        var execContext = new SpecExecutionContext("test-spec", suite: suite);
        _context = execContext;
    }

    public async Task InitializeAsync() => await _resource.Start();
    public async Task DisposeAsync() => await _resource.DisposeAsync();

    [Fact]
    public void get_wolverine_resource_returns_resource()
    {
        _context.GetWolverineResource().ShouldBeSameAs(_resource);
    }

    [Fact]
    public void get_wolverine_host_returns_host()
    {
        _context.GetWolverineHost().ShouldBeSameAs(_resource.Host);
    }

    [Fact]
    public async Task invoke_message_and_wait_executes_handler()
    {
        var session = await _context.InvokeMessageAndWaitAsync(new PingMessage("hello"));

        session.ShouldNotBeNull();
        session.Executed.SingleMessage<PingMessage>().ShouldNotBeNull();
    }

    [Fact]
    public async Task invoke_message_and_wait_stores_last_session()
    {
        await _context.InvokeMessageAndWaitAsync(new PingMessage("hello"));

        _resource.LastSession.ShouldNotBeNull();
    }

    [Fact]
    public async Task last_tracked_session_returns_stored_session()
    {
        await _context.InvokeMessageAndWaitAsync(new PingMessage("hello"));

        var session = _context.LastTrackedSession();
        session.ShouldBeSameAs(_resource.LastSession);
    }

    [Fact]
    public void last_tracked_session_throws_when_no_session()
    {
        Should.Throw<InvalidOperationException>(() => _context.LastTrackedSession());
    }

    [Fact]
    public async Task invoke_message_and_wait_with_response_returns_response()
    {
        var (session, response) = await _context.InvokeMessageAndWaitAsync<ItemCreated>(
            new CreateItem("Widget"));

        session.ShouldNotBeNull();
        response.ShouldNotBeNull();
        response!.Name.ShouldBe("Widget");
        response.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task invoke_message_and_wait_with_response_stores_last_session()
    {
        await _context.InvokeMessageAndWaitAsync<ItemCreated>(new CreateItem("Widget"));

        _resource.LastSession.ShouldNotBeNull();
    }

    [Fact]
    public async Task send_message_and_wait_executes_handler()
    {
        var session = await _context.SendMessageAndWaitAsync(new PingMessage("ping"));

        session.ShouldNotBeNull();
        session.Executed.SingleMessage<PingMessage>().ShouldNotBeNull();
    }

    [Fact]
    public async Task send_message_and_wait_stores_last_session()
    {
        await _context.SendMessageAndWaitAsync(new PingMessage("ping"));

        _resource.LastSession.ShouldNotBeNull();
    }

    [Fact]
    public async Task execute_and_wait_stores_last_session()
    {
        var session = await _context.ExecuteAndWaitAsync(
            async ctx => await ctx.SendAsync(new PingMessage("via execute")));

        session.ShouldNotBeNull();
        _resource.LastSession.ShouldBeSameAs(session);
    }
}
