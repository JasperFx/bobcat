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

    public TrackingResource(string name, List<string> log)
    {
        Name = name;
        _log = log;
    }

    public string Name { get; }

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
