using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine.Tracking;

namespace Bobcat.CritterStack.Tests;

public class CritterStackTests
{
    public class Account; // sample aggregate type

    [Fact]
    public void aggregate_execution_carries_session_events_and_aggregate()
    {
        var session = Substitute.For<ITrackedSession>();
        var events = new List<IEvent>();
        var aggregate = new Account();

        var execution = new AggregateExecution<Account>(session, events, aggregate);

        execution.Session.ShouldBeSameAs(session);
        execution.NewEvents.ShouldBeSameAs(events);
        execution.Aggregate.ShouldBeSameAs(aggregate);

        var (s, e, a) = execution; // record deconstruction
        s.ShouldBeSameAs(session);
        e.ShouldBeSameAs(events);
        a.ShouldBeSameAs(aggregate);
    }

    private static IStepContext emptyContext()
        => new SpecExecutionContext("spec", suite: new TestSuite());

    [Fact]
    public async Task reset_requires_a_host_resource_and_reports_when_missing()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => emptyContext().ResetCritterStackAsync());

        ex.Message.ShouldContain("IHostResource");
    }

    [Fact]
    public async Task execute_aggregate_command_requires_a_host_resource()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => emptyContext().ExecuteAggregateCommandAsync<Account>(new object(), Guid.NewGuid()));
    }

    [Fact]
    public async Task wait_for_projection_requires_a_host_resource()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => emptyContext().WaitForProjectionAsync<Account>(minSequence: 1));
    }

    // --- store resolution from the host's container ------------------------------------------

    [Fact]
    public async Task a_host_with_no_event_store_says_so()
    {
        await using var resource = hostWith(_ => { });
        await resource.Start();
        var context = contextFor(resource);

        var ex = Should.Throw<InvalidOperationException>(() => context.EventStore());

        ex.Message.ShouldContain("No JasperFx.Events.IEventStore is registered");
        ex.Message.ShouldContain("AddMarten");
        ex.Message.ShouldContain("AddFisher");
    }

    [Fact]
    public async Task the_only_event_store_is_found_without_naming_it()
    {
        var store = fakeStore("orders", "fake");
        await using var resource = hostWith(s => s.AddSingleton(store));
        await resource.Start();

        contextFor(resource).EventStore().ShouldBeSameAs(store);
    }

    [Fact]
    public async Task several_event_stores_need_a_name_and_are_matched_by_identity()
    {
        var orders = fakeStore("orders", "fake");
        var billing = fakeStore("billing", "fake");
        await using var resource = hostWith(s => s.AddSingleton(orders).AddSingleton(billing));
        await resource.Start();
        var context = contextFor(resource);

        var ex = Should.Throw<InvalidOperationException>(() => context.EventStore());
        ex.Message.ShouldContain("2 event stores");
        ex.Message.ShouldContain("'orders'");
        ex.Message.ShouldContain("'billing'");

        context.EventStore(storeName: "Billing").ShouldBeSameAs(billing);

        Should.Throw<InvalidOperationException>(() => context.EventStore(storeName: "nope"))
            .Message.ShouldContain("No event store named 'nope'");
    }

    [Fact]
    public async Task reset_resets_every_store_the_host_registers()
    {
        var orders = new ResettableStore("orders");
        var billing = new ResettableStore("billing");
        await using var resource = hostWith(s => s.AddSingleton<IEventStore>(orders).AddSingleton<IEventStore>(billing));
        await resource.Start();

        await contextFor(resource).ResetCritterStackAsync();

        orders.Advanced.Resets.ShouldBe(1);
        billing.Advanced.Resets.ShouldBe(1);

        // And the host-level form a reset hook would use.
        await resource.Host.ResetEventStoresAsync();
        orders.Advanced.Resets.ShouldBe(2);
    }

    private static IEventStore fakeStore(string name, string type)
    {
        var store = Substitute.For<IEventStore>();
        store.Identity.Returns(new EventStoreIdentity(name, type));
        return store;
    }

    private static HostResource hostWith(Action<IServiceCollection> configure)
        => new(() =>
        {
            var builder = Host.CreateApplicationBuilder();
            configure(builder.Services);
            return builder.Build();
        });

    private static IStepContext contextFor(IHostResource resource)
    {
        var context = Substitute.For<IStepContext>();
        context.GetResource<IHostResource>(null).Returns(resource);
        context.Cancellation.Returns(CancellationToken.None);
        return context;
    }

    /// <summary>
    /// An IEventStore shaped like the Critter Stack stores for reset purposes: a public
    /// <c>Advanced.ResetAllData(CancellationToken)</c>, which is what the convention looks for.
    /// </summary>
    private sealed class ResettableStore : FakeEventStore
    {
        public ResettableStore(string name) : base(name) { }

        public AdvancedOperations Advanced { get; } = new();

        public sealed class AdvancedOperations
        {
            public int Resets { get; private set; }

            public Task ResetAllData(CancellationToken token)
            {
                Resets++;
                return Task.CompletedTask;
            }
        }
    }
}
