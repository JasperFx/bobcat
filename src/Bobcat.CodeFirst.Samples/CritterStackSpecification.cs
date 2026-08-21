using Bobcat.CritterStack;
using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Bobcat.Runtime;
using Marten;

namespace Bobcat.CodeFirst.Samples;

/// <summary>
/// TO BE REPLACED BY <c>CritterStackFixture</c> (issue #104). A minimal, sample-local version of the
/// typed event-sourcing steps that issue describes — <c>GivenStream</c>, <c>WhenCommand</c>,
/// <c>ThenNewEvents</c>, <c>ThenMessageSent</c> — built on the code-first API so the aggregate port
/// could be written against the vocabulary the shipped fixture will have. When #104 lands, a
/// specification hosts that fixture (<see cref="Specification.Host{TFixture}"/>) or inherits a base
/// that does, and this file is deleted.
/// </summary>
/// <remarks>
/// Everything that reads the store goes through <c>Bobcat.CritterStack</c>'s store-agnostic helpers.
/// The one place this file touches Marten directly is <see cref="GivenStream{TAggregate}"/>:
/// appending events has no <c>JasperFx.Events</c> abstraction at the repo's pin (see CLAUDE.md,
/// "Bobcat.CritterStack is store-agnostic"), and a sample project referencing Marten is allowed to.
/// </remarks>
public abstract class CritterStackSpecification : Specification
{
    /// <summary>The named <see cref="IHostResource"/> these steps talk to.</summary>
    protected virtual string HostName => Hosts.App;

    /// <summary>
    /// Start a stream with these events. Renders each event as a row — records are self-describing,
    /// so a marker event shows its name and an event with state shows its properties.
    /// </summary>
    protected StepHandle GivenStream<TAggregate>(Guid streamId, params object[] events) where TAggregate : class
        => Given($"a {typeof(TAggregate).Name} stream {Short(streamId)} with these events", async ctx =>
            {
                var store = ctx.GetRootService<IDocumentStore>(HostName);
                await using var session = store.LightweightSession();
                session.Events.StartStream<TAggregate>(streamId, events);
                await session.SaveChangesAsync(ctx.Cancellation);
            })
            .WithRows(events);

    /// <summary>
    /// Send a command through Wolverine, wait for the tracked session to settle, and capture the
    /// events it appended to the aggregate's stream plus the rebuilt aggregate. The command renders
    /// as the step's input row.
    /// </summary>
    protected Captured<AggregateExecution<TAggregate>> WhenCommand<TAggregate>(object command, Guid aggregateId, int timeoutInMilliseconds = 5000)
        where TAggregate : class
        => When($"{command.GetType().Name} is received for {Short(aggregateId)}",
                ctx => ctx.ExecuteAggregateCommandAsync<TAggregate>(command, aggregateId, HostName, timeoutInMilliseconds: timeoutInMilliseconds))
            .WithRows(command);

    /// <summary>
    /// Exactly these event types were appended by the command, in this order. Renders as a table of
    /// expected versus actual event names, one row per position.
    /// </summary>
    protected StepHandle ThenNewEvents<TAggregate>(Captured<AggregateExecution<TAggregate>> run, params Type[] expected)
        => Step(StepKind.Then, "these events are appended", (_, result, _) =>
        {
            var actual = run.Value.NewEvents.Select(e => e.EventType.Name).ToList();
            var cells = new List<CellResult>();
            var rows = Math.Max(actual.Count, expected.Length);

            for (var i = 0; i < rows; i++)
            {
                var expectedName = i < expected.Length ? expected[i].Name : null;
                var actualName = i < actual.Count ? actual[i] : null;

                cells.Add(expectedName is null
                    ? new CellResult("event", ResultStatus.invalid, $"Extra row: event={actualName}") { RowIndex = i }
                    : actualName is null
                        ? new CellResult("missing-row", ResultStatus.missing, $"Expected row not found: event={expectedName}") { RowIndex = i }
                        : CellCheck.For("event", actualName, expectedName, rowIndex: i));
            }

            result.IsSetVerification = true;
            result.SetVerificationColumns = ["event"];
            result.MarkCells(cells.ToArray());
            if (cells.All(c => c.Status == ResultStatus.success)) result.MarkSuccess();
            else result.MarkFailed();
            return Task.CompletedTask;
        });

    protected void ThenNoNewEvents<TAggregate>(Captured<AggregateExecution<TAggregate>> run)
        => Then("the number of events appended", () => run.Value.NewEvents.Count).ShouldBe(0);

    /// <summary>One message of this type was sent (cascaded) while the command ran.</summary>
    protected ValueExpectation<TMessage> ThenMessageSent<TAggregate, TMessage>(Captured<AggregateExecution<TAggregate>> run, string? described = null)
        where TMessage : class
        => Then(described ?? $"the {typeof(TMessage).Name} that was sent", () => run.Value.Session.Sent.SingleMessage<TMessage>());

    protected void ThenNoMessagesSent<TAggregate>(Captured<AggregateExecution<TAggregate>> run)
        => Then("no messages are sent", () => run.Value.Session.Sent.AllMessages().Count()).ShouldBe(0);

    /// <summary>The first block of a Guid — enough to tell two streams apart in a report.</summary>
    protected static string Short(Guid id) => id.ToString("N")[..8];
}
