namespace Bobcat.CodeFirst.Samples;

/// <summary>
/// Port of Wolverine's <c>MartenTests/AggregateHandlerWorkflow/aggregate_handler_workflow.cs</c> —
/// the <c>[AggregateHandler]</c> command workflow: a command arrives, the aggregate is fetched for
/// writing, the handler returns events, a response and cascaded messages, and the inline snapshot
/// catches up. One xUnit class with a host per class became one feature with a shared host.
/// </summary>
[FixtureTitle("Aggregate handler workflow")]
public class AggregateHandlerWorkflowSpecs : CritterStackSpecification
{
    // aggregate_handler_workflow.cs:87 events_then_response_invoke_no_return (and :134, the with-return twin)
    [Scenario("Events then response")]
    public void events_then_response()
    {
        var id = Guid.NewGuid();
        GivenStream<LetterAggregate>(id, new LetterStarted());

        var run = WhenCommand<LetterAggregate>(new RaiseABC(id), id);

        ThenNewEvents(run, typeof(AEvent), typeof(BEvent), typeof(CEvent));
        ThenMessageSent<LetterAggregate, Response>(run).ShouldSatisfy(r => r.ACount == 1, "carry ACount 1");
        Then("the aggregate's ACount", () => run.Value.Aggregate!.ACount).ShouldBe(1);
        Then("the aggregate's BCount", () => run.Value.Aggregate!.BCount).ShouldBe(1);
        Then("the aggregate's CCount", () => run.Value.Aggregate!.CCount).ShouldBe(1);
    }

    // aggregate_handler_workflow.cs:151 response_then_events_invoke_no_return
    [Scenario("Response then events")]
    public void response_then_events()
    {
        var id = Guid.NewGuid();
        GivenStream<LetterAggregate>(id, new LetterStarted());

        var run = WhenCommand<LetterAggregate>(new RaiseAABCC(id), id);

        ThenNewEvents(run, typeof(AEvent), typeof(AEvent), typeof(BEvent), typeof(CEvent), typeof(CEvent));
        ThenMessageSent<LetterAggregate, Response>(run).ShouldSatisfy(r => r.ACount == 2, "carry ACount 2");
        Then("the aggregate's ACount", () => run.Value.Aggregate!.ACount).ShouldBe(2);
        Then("the aggregate's BCount", () => run.Value.Aggregate!.BCount).ShouldBe(1);
        Then("the aggregate's CCount", () => run.Value.Aggregate!.CCount).ShouldBe(2);
    }

    // aggregate_handler_workflow.cs:185 return_mix_of_events_messages_and_response
    [Scenario("A mix of events, messages and a response")]
    public void return_mix_of_events_messages_and_response()
    {
        var id = Guid.NewGuid();
        GivenStream<LetterAggregate>(id, new LetterStarted());

        var run = WhenCommand<LetterAggregate>(new RaiseBBCCC(id), id);

        ThenNewEvents(run, typeof(BEvent), typeof(BEvent), typeof(CEvent), typeof(CEvent), typeof(CEvent));
        // "Just proves that this is what comes out of the handler" — the original's words.
        ThenMessageSent<LetterAggregate, Response>(run).ShouldSatisfy(r => r.ACount == 5, "carry ACount 5");
        ThenMessageSent<LetterAggregate, LetterMessage1>(run).ShouldNotBeNull();
        ThenMessageSent<LetterAggregate, LetterMessage2>(run).ShouldNotBeNull();
        Then("the aggregate's ACount", () => run.Value.Aggregate!.ACount).ShouldBe(0);
        Then("the aggregate's BCount", () => run.Value.Aggregate!.BCount).ShouldBe(2);
        Then("the aggregate's CCount", () => run.Value.Aggregate!.CCount).ShouldBe(3);
    }

    // aggregate_handler_workflow.cs:287 using_the_aggregate_in_a_before_method
    [Scenario("A Validate method can see the aggregate and stop the handler")]
    public void using_the_aggregate_in_a_before_method()
    {
        var validated = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        GivenStream<LetterAggregate>(validated, new AEvent(), new CEvent());
        GivenStream<LetterAggregate>(fresh, new CEvent(), new CEvent());

        var stopped = WhenCommand<LetterAggregate>(new RaiseIfValidated(validated), validated);
        var continued = WhenCommand<LetterAggregate>(new RaiseIfValidated(fresh), fresh);

        // Validate stops the handler when ACount is already set, so nothing is appended there...
        ThenNoNewEvents(stopped);
        Then("the validated stream's BCount", () => stopped.Value.Aggregate!.BCount).ShouldBe(0);
        // ...and lets it through when it is not.
        ThenNewEvents(continued, typeof(BEvent));
        Then("the fresh stream's BCount", () => continued.Value.Aggregate!.BCount).ShouldBe(1);
    }
}
