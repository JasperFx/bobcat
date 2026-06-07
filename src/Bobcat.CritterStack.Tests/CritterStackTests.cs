using Bobcat.CritterStack;
using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx.Events;
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

    private static IStepContext EmptyContext()
        => new SpecExecutionContext("spec", suite: new TestSuite());

    [Fact]
    public async Task reset_resolves_marten_resource_and_reports_when_missing()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => EmptyContext().ResetCritterStackAsync());
    }

    [Fact]
    public async Task execute_aggregate_command_requires_a_marten_resource()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => EmptyContext().ExecuteAggregateCommandAsync<Account>(new object(), Guid.NewGuid()));
    }

    [Fact]
    public async Task wait_for_projection_requires_a_marten_resource()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => EmptyContext().WaitForProjectionAsync<Account>(Guid.NewGuid(), minSequence: 1));
    }
}
