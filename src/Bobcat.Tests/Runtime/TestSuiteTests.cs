using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

public class TestSuiteTests
{
    [Fact]
    public async Task starts_resources_in_registration_order()
    {
        var order = new List<string>();
        var suite = new TestSuite();
        suite.AddResource(new TrackingResource("first", order));
        suite.AddResource(new TrackingResource("second", order));
        suite.AddResource(new TrackingResource("third", order));

        await suite.StartAll();

        order.ShouldBe(["first:start", "second:start", "third:start"]);
    }

    [Fact]
    public async Task disposes_in_reverse_order()
    {
        var order = new List<string>();
        var suite = new TestSuite();
        suite.AddResource(new TrackingResource("first", order));
        suite.AddResource(new TrackingResource("second", order));
        suite.AddResource(new TrackingResource("third", order));

        await suite.StartAll();
        order.Clear();

        await suite.DisposeAsync();

        order.ShouldBe(["third:dispose", "second:dispose", "first:dispose"]);
    }

    [Fact]
    public async Task resets_all_resources()
    {
        var order = new List<string>();
        var suite = new TestSuite();
        suite.AddResource(new TrackingResource("a", order));
        suite.AddResource(new TrackingResource("b", order));

        await suite.StartAll();
        order.Clear();

        await suite.ResetAll();

        order.ShouldBe(["a:reset", "b:reset"]);
    }

    [Fact]
    public async Task start_failure_throws_catastrophic()
    {
        var suite = new TestSuite();
        suite.AddResource(new FailingResource("bad"));

        var ex = await Should.ThrowAsync<SpecCatastrophicException>(suite.StartAll());
        ex.Message.ShouldContain("bad");
        ex.Message.ShouldContain("failed to start");
    }

    [Fact]
    public async Task a_start_failure_leaves_the_resources_before_it_up_and_the_ones_after_it_untouched()
    {
        var order = new List<string>();
        var suite = new TestSuite();
        suite.AddResource(new TrackingResource("first", order));
        suite.AddResource(new TrackingResource("second", order, failOnStart: true));
        suite.AddResource(new TrackingResource("third", order));

        await Should.ThrowAsync<SpecCatastrophicException>(suite.StartAll());
        await suite.DisposeAsync();

        // "third" was never asked to start, so it is never asked to dispose either — its
        // DisposeAsync was written assuming Start ran. "second" may be half up, so it is.
        order.ShouldBe(["first:start", "second:start", "second:dispose", "first:dispose"]);
    }

    [Fact]
    public async Task every_resource_is_disposed_even_when_one_throws_and_the_failure_surfaces_after()
    {
        var order = new List<string>();
        var suite = new TestSuite();
        suite.AddResource(new TrackingResource("first", order));
        suite.AddResource(new TrackingResource("second", order, failOnDispose: true));
        suite.AddResource(new TrackingResource("third", order));

        await suite.StartAll();
        order.Clear();

        var ex = await Should.ThrowAsync<AggregateException>(async () => await suite.DisposeAsync());

        ex.InnerExceptions.ShouldHaveSingleItem().Message.ShouldContain("second");
        order.ShouldBe(["third:dispose", "second:dispose", "first:dispose"]);
    }

    [Fact]
    public void get_resource_by_type()
    {
        var suite = new TestSuite();
        var resource = new TrackingResource("tracker", new List<string>());
        suite.AddResource(resource);

        suite.GetResource<TrackingResource>().ShouldBe(resource);
    }

    [Fact]
    public void get_resource_by_name()
    {
        var suite = new TestSuite();
        var first = new TrackingResource("first", new List<string>());
        var second = new TrackingResource("second", new List<string>());
        suite.AddResource(first);
        suite.AddResource(second);

        suite.GetResource<TrackingResource>("first").ShouldBe(first);
        suite.GetResource<TrackingResource>("second").ShouldBe(second);
    }

    [Fact]
    public void get_resource_by_type_throws_when_multiple()
    {
        var suite = new TestSuite();
        suite.AddResource(new TrackingResource("a", new List<string>()));
        suite.AddResource(new TrackingResource("b", new List<string>()));

        Should.Throw<InvalidOperationException>(() => suite.GetResource<TrackingResource>())
            .Message.ShouldContain("Multiple");
    }

    [Fact]
    public void get_resource_throws_when_not_found()
    {
        var suite = new TestSuite();

        Should.Throw<InvalidOperationException>(() => suite.GetResource<TrackingResource>())
            .Message.ShouldContain("No resource");
    }

    [Fact]
    public void duplicate_name_throws()
    {
        var suite = new TestSuite();
        suite.AddResource(new TrackingResource("same", new List<string>()));

        Should.Throw<ArgumentException>(() =>
            suite.AddResource(new TrackingResource("same", new List<string>())))
            .Message.ShouldContain("same");
    }

    [Fact]
    public async Task opens_scenario_scopes_in_registration_order_and_closes_in_reverse()
    {
        var order = new List<string>();
        var suite = new TestSuite();
        suite.AddResource(new TrackingResource("plain", order));
        suite.AddResource(new TrackingHostResource("first", order));
        suite.AddResource(new TrackingHostResource("second", order));

        await suite.BeginScenarioAll();
        await suite.EndScenarioAll();

        // The plain (non-host) resource has no DI container and is skipped entirely.
        order.ShouldBe(["first:begin", "second:begin", "second:end", "first:end"]);
    }

    [Fact]
    public async Task resource_accessible_from_step_context()
    {
        var suite = new TestSuite();
        var resource = new TrackingResource("tracker", new List<string>());
        suite.AddResource(resource);
        await suite.StartAll();

        var context = new SpecExecutionContext("test", suite: suite);
        context.GetResource<TrackingResource>().ShouldBe(resource);
    }
}

internal class TrackingResource : ITestResource
{
    private readonly List<string> _log;
    private readonly bool _failOnStart;
    private readonly bool _failOnDispose;

    public TrackingResource(string name, List<string> log, bool failOnStart = false, bool failOnDispose = false)
    {
        Name = name;
        _log = log;
        _failOnStart = failOnStart;
        _failOnDispose = failOnDispose;
    }

    public string Name { get; }

    public Task Start()
    {
        _log.Add($"{Name}:start");
        if (_failOnStart) throw new InvalidOperationException($"{Name} would not start");
        return Task.CompletedTask;
    }

    public Task ResetBetweenScenarios()
    {
        _log.Add($"{Name}:reset");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _log.Add($"{Name}:dispose");
        if (_failOnDispose) throw new InvalidOperationException($"{Name} would not dispose");
        return ValueTask.CompletedTask;
    }
}

internal class TrackingHostResource : IHostResource
{
    private readonly List<string> _log;

    public TrackingHostResource(string name, List<string> log)
    {
        Name = name;
        _log = log;
    }

    public string Name { get; }
    public Microsoft.Extensions.Hosting.IHost Host => throw new NotSupportedException();
    public IServiceProvider RootServices => throw new NotSupportedException();
    public IServiceProvider CurrentServices => throw new NotSupportedException();

    public Task Start()
    {
        _log.Add($"{Name}:start");
        return Task.CompletedTask;
    }

    public Task ResetBetweenScenarios()
    {
        _log.Add($"{Name}:reset");
        return Task.CompletedTask;
    }

    public ValueTask BeginScenarioScope()
    {
        _log.Add($"{Name}:begin");
        return ValueTask.CompletedTask;
    }

    public ValueTask EndScenarioScope()
    {
        _log.Add($"{Name}:end");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _log.Add($"{Name}:dispose");
        return ValueTask.CompletedTask;
    }
}

internal class FailingResource : ITestResource
{
    public FailingResource(string name) => Name = name;
    public string Name { get; }
    public Task Start() => throw new Exception("Connection refused");
    public Task ResetBetweenScenarios() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
