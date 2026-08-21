using Wolverine;
using Wolverine.Marten;

namespace Bobcat.CodeFirst.Samples;

// The domain of Wolverine's MartenTests/AggregateHandlerWorkflow/aggregate_handler_workflow.cs,
// trimmed to what the ported scenarios use. Copied rather than referenced: the point is to port
// the tests, and the tests own their domain.

public record LetterStarted;
public record AEvent;
public record BEvent;
public record CEvent;
public record DEvent;

public class LetterAggregate
{
    public LetterAggregate()
    {
    }

    public LetterAggregate(LetterStarted started)
    {
    }

    public Guid Id { get; set; }
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }

    public void Apply(AEvent e) => ACount++;
    public void Apply(BEvent e) => BCount++;
    public void Apply(CEvent e) => CCount++;
    public void Apply(DEvent e) => DCount++;
}

public record RaiseABC(Guid LetterAggregateId);
public record RaiseAABCC(Guid LetterAggregateId);
public record RaiseBBCCC(Guid LetterAggregateId);
public record RaiseIfValidated(Guid LetterAggregateId);

public record LetterMessage1;
public record LetterMessage2;

public class Response
{
    public static Response For(LetterAggregate aggregate) => new()
    {
        ACount = aggregate.ACount,
        BCount = aggregate.BCount,
        CCount = aggregate.CCount,
        DCount = aggregate.DCount
    };

    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
}

public static class ResponseHandler
{
    public static void Handle(Response cmd) { }
    public static void Handle(LetterMessage1 cmd) { }
    public static void Handle(LetterMessage2 cmd) { }
}

[AggregateHandler]
public static class RaiseLetterHandler
{
    public static (object[], Response) Handle(RaiseABC command, LetterAggregate aggregate)
    {
        aggregate.ACount++;
        aggregate.BCount++;
        aggregate.CCount++;
        return ([new AEvent(), new BEvent(), new CEvent()], Response.For(aggregate));
    }

    public static (Response, Events) Handle(RaiseAABCC command, LetterAggregate aggregate)
    {
        aggregate.ACount += 2;
        aggregate.BCount++;
        aggregate.CCount += 2;

        return (Response.For(aggregate), [new AEvent(), new AEvent(), new BEvent(), new CEvent(), new CEvent()]);
    }

    public static (Response, Events, OutgoingMessages) Handle(RaiseBBCCC command, LetterAggregate aggregate)
    {
        var events = new Events { new BEvent(), new BEvent(), new CEvent(), new CEvent(), new CEvent() };
        var messages = new OutgoingMessages { new LetterMessage1(), new LetterMessage2() };

        return (new Response { ACount = 5 }, events, messages);
    }
}

public static class RaiseIfValidatedHandler
{
    public static HandlerContinuation Validate(LetterAggregate aggregate) =>
        aggregate.ACount == 0 ? HandlerContinuation.Continue : HandlerContinuation.Stop;

    [AggregateHandler]
    public static IEnumerable<object> Handle(RaiseIfValidated command, LetterAggregate aggregate)
    {
        yield return new BEvent();
    }
}
