using Bobcat.Runtime;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Marten;

namespace Bobcat.CodeFirst.Samples;

/// <summary>
/// Port of Wolverine's <c>MartenTests/MartenOutbox_end_to_end.cs</c>: a document write and an
/// outgoing message committed in one Marten transaction through <see cref="IMartenOutbox"/>, with
/// the message then delivered through a durable local queue.
/// </summary>
[FixtureTitle("Marten outbox")]
public class MartenOutboxSpecs : Specification
{
    private Task<OutboxedMessage> _delivery = Task.FromException<OutboxedMessage>(new InvalidOperationException("not armed"));

    // MartenOutbox_end_to_end.cs:39 persist_and_send_message_one_tx
    [Scenario("A document and an outgoing message commit in one transaction")]
    public void persist_and_send_message_one_tx()
    {
        var id = Guid.NewGuid();

        // Arm the handler first; a step body that returns a Task is awaited, so the waiter is held in
        // a field rather than captured.
        Given("a handler waiting for the next OutboxedMessage", () => { _delivery = OutboxedMessageHandler.WaitForNextMessage(); });

        When("an Item is stored and an OutboxedMessage is published through the outbox in the same session", async ctx =>
            {
                // The scenario's own DI scope: the same scoped session and outbox any step in this
                // scenario would see, disposed when the scenario ends.
                var services = ctx.ScenarioServices(Hosts.App);
                var outbox = services.GetRequiredService<IMartenOutbox>();
                var session = services.GetRequiredService<IDocumentSession>();
                outbox.Enroll(session);

                session.Store(new Item { Id = id });
                await outbox.PublishAsync(new OutboxedMessage { Id = id });

                await session.SaveChangesAsync(ctx.Cancellation);
            })
            .WithRows(new Item { Id = id }, new OutboxedMessage { Id = id });

        Then("the id on the message the handler received", async () => (await _delivery).Id).ShouldBe(id);

        Then("the Item is in the database", async ctx =>
        {
            await using var query = ctx.GetRootService<IDocumentStore>(Hosts.App).QuerySession();
            return await query.LoadAsync<Item>(id, ctx.Cancellation);
        }).ShouldNotBeNull();
    }
}

// --- the domain, from MartenOutbox_end_to_end.cs ---------------------------------------------------

public class Item
{
    public Guid Id { get; set; }
}

public record OutboxedMessage
{
    public Guid Id { get; set; }
}

public class OutboxedMessageHandler
{
    private static TaskCompletionSource<OutboxedMessage> _source = new();

    public static Task<OutboxedMessage> WaitForNextMessage()
    {
        _source = new TaskCompletionSource<OutboxedMessage>();
        return _source.Task.WaitAsync(15.Seconds());
    }

    public void Handle(OutboxedMessage message)
    {
        _source?.TrySetResult(message);
    }
}
