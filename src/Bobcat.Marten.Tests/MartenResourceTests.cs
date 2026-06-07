using Bobcat.Engine;
using Bobcat.Runtime;
using Marten;
using Shouldly;

namespace Bobcat.Marten.Tests;

public class MartenResourceTests
{
    // A lazy store — constructed but never connected (no Postgres needed for these tests).
    private static IDocumentStore LazyStore()
        => DocumentStore.For("Host=localhost;Port=5432;Database=bobcat_test;Username=postgres;Password=postgres");

    [Fact]
    public void implements_marker_and_test_resource()
    {
        var resource = new MartenResource(LazyStore);
        resource.ShouldBeAssignableTo<IMartenResource>();
        resource.ShouldBeAssignableTo<ITestResource>();
    }

    [Fact]
    public void default_name_is_marten()
    {
        new MartenResource(LazyStore).Name.ShouldBe("Marten");
    }

    [Fact]
    public void custom_name_is_used()
    {
        new MartenResource(LazyStore, name: "Events").Name.ShouldBe("Events");
    }

    [Fact]
    public void document_store_throws_before_start()
    {
        var resource = new MartenResource(LazyStore);
        Should.Throw<InvalidOperationException>(() => _ = resource.DocumentStore);
    }

    [Fact]
    public async Task start_exposes_the_store()
    {
        var store = LazyStore();
        var resource = new MartenResource(store);
        await resource.Start();
        resource.DocumentStore.ShouldBeSameAs(store);
        await resource.DisposeAsync();
    }

    [Fact]
    public async Task reset_invokes_custom_delegate()
    {
        var called = false;
        var resource = new MartenResource(LazyStore(), reset: _ => { called = true; return Task.CompletedTask; });
        await resource.Start();

        await resource.ResetBetweenScenarios();

        called.ShouldBeTrue();
        await resource.DisposeAsync();
    }

    [Fact]
    public async Task resource_is_discoverable_through_step_context_by_marker_interface()
    {
        var resource = new MartenResource(LazyStore());
        var suite = new TestSuite();
        suite.AddResource(resource);

        IStepContext context = new SpecExecutionContext("spec", suite: suite);

        context.GetResource<IMartenResource>().ShouldBeSameAs(resource);
        await resource.DisposeAsync();
    }

    [Fact]
    public async Task resource_is_discoverable_by_name()
    {
        var a = new MartenResource(LazyStore(), name: "primary");
        var b = new MartenResource(LazyStore(), name: "secondary");
        var suite = new TestSuite();
        suite.AddResource(a);
        suite.AddResource(b);

        IStepContext context = new SpecExecutionContext("spec", suite: suite);

        context.GetResource<IMartenResource>("secondary").ShouldBeSameAs(b);
        await a.DisposeAsync();
        await b.DisposeAsync();
    }
}
