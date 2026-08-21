using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Wolverine;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Tracking;

namespace Bobcat.CodeFirst.Samples;

/// <summary>
/// Port of Wolverine's <c>MartenTests/Saga/OrderSagaTests.cs</c> (<c>When_starting_an_order</c>)
/// over the <c>OrderSagaSample</c> saga, extended to the two flows the original stops short of:
/// completing the order, and the scheduled <see cref="OrderTimeout"/> firing. A Marten-persisted
/// saga, a tracked session, and a scheduled message played forward — the message-handling shape.
/// </summary>
[FixtureTitle("Order saga")]
public class OrderSagaSpecs : Specification
{
    // OrderSagaTests.cs:15 When_starting_an_order (should_exist, should_not_be_completed)
    [Scenario("Starting an order")]
    public void starting_an_order()
    {
        var orderId = Guid.NewGuid().ToString();

        var run = When("StartOrder is received", ctx => ctx.InvokeMessageAndWaitAsync(new StartOrder(orderId), Hosts.App))
            .WithRows(new StartOrder(orderId));

        Then("the Order saga document", ctx => load(ctx, orderId)).ShouldNotBeNull();
        Then("whether the order is completed", async ctx => (await load(ctx, orderId))!.IsCompleted()).ShouldBe(false);
        Then("the OrderTimeout scheduled by the saga", () => run.Value.Scheduled.SingleMessage<OrderTimeout>().Id).ShouldBe(orderId);
    }

    [Scenario("Completing an order")]
    public void completing_an_order()
    {
        var orderId = Guid.NewGuid().ToString();

        Given("a started order", ctx => ctx.InvokeMessageAndWaitAsync(new StartOrder(orderId), Hosts.App))
            .WithRows(new StartOrder(orderId));

        When("CompleteOrder is received", ctx => ctx.InvokeMessageAndWaitAsync(new CompleteOrder(orderId), Hosts.App))
            .WithRows(new CompleteOrder(orderId));

        Then("the Order saga document", ctx => load(ctx, orderId)).ShouldBeNull();
    }

    [Scenario("An order times out")]
    public void an_order_times_out()
    {
        var orderId = Guid.NewGuid().ToString();

        var started = Given("a started order", ctx => ctx.InvokeMessageAndWaitAsync(new StartOrder(orderId), Hosts.App))
            .WithRows(new StartOrder(orderId));

        // The timeout is scheduled a minute out; the tracked session can play it now.
        When("the scheduled OrderTimeout is played", () => started.Value.PlayScheduledMessagesAsync(10.Seconds()));

        Then("the Order saga document", ctx => load(ctx, orderId)).ShouldBeNull();
    }

    private static async Task<Order?> load(IStepContext ctx, string orderId)
    {
        await using var session = ctx.GetRootService<IDocumentStore>(Hosts.App).QuerySession();
        return await session.LoadAsync<Order>(orderId, ctx.Cancellation);
    }
}

// --- the domain, from src/Samples/OrderSagaSample/OrderSaga.cs ---------------------------------------

public record StartOrder(string OrderId);
public record CompleteOrder(string Id);

/// <summary>Always scheduled to be delivered after a one minute delay.</summary>
public record OrderTimeout(string Id) : TimeoutMessage(1.Minutes());

public class Order : Saga
{
    public string? Id { get; set; }

    public static (Order, OrderTimeout) Start(StartOrder order, ILogger<Order> logger)
    {
        logger.LogInformation("Got a new order with id {Id}", order.OrderId);
        return (new Order { Id = order.OrderId }, new OrderTimeout(order.OrderId));
    }

    public void Handle(CompleteOrder complete, ILogger<Order> logger)
    {
        logger.LogInformation("Completing order {Id}", complete.Id);
        MarkCompleted();
    }

    public void Handle(OrderTimeout timeout, ILogger<Order> logger)
    {
        logger.LogInformation("Applying timeout to order {Id}", timeout.Id);
        MarkCompleted();
    }

    public static void NotFound(CompleteOrder complete, ILogger<Order> logger)
    {
        logger.LogInformation("Tried to complete order {Id}, but it cannot be found", complete.Id);
    }
}
