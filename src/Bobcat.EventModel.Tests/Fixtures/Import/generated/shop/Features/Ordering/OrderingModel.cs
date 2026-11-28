// Imported from an eventmodelers.ai board by `bobcat import-event-model`.
//
// This is ordinary C# and nothing regenerates it. The segmentation an import performs is a
// set of reported guesses, so a wrong guess is a one-line edit here rather than a re-import.

using JasperFx.Events.EventModeling;
using Shop;

namespace Shop.Ordering;

public class OrderingModel : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        model.InChapter("Ordering");
        // ⚠ inferred: 2 of the 2 commands here decide against Order, so it is the
        // default; .Against<T>() on a slice replaces it, and .NoAggregate() says it has none.
        // #region identified-by
        model.ForAggregate<Order>().IdentifiedBy<OrderId>();
        // #endregion identified-by

        // From chapter 'Ordering', actor 'Member'.
        // ⚠ no example says whether it starts the Order stream, so it is assumed to append to one.
        // ⚠ inferred: decides against Order — its examples give Order events before it appends; 'Order' is the subject 2 of its 2 events share.
        model.Command<PlaceOrder>()
            .Emits<OrderPlaced>()
            .OnStream<Order>();

        // From chapter 'Ordering', actor 'Member'.
        // ⚠ inferred: decides against Order — its examples give Order events before it appends; 'Order' is the subject 2 of its 2 events share.
        model.Command<CancelOrder>()
            .Emits<OrderCancelled>()
            .OnStream<Order>()
            .LinksToSpecification("CancelOrder/an order is cancelled");

        // From chapter 'Ordering', actor 'Member'.
        model.View<OrderSummary>()
            .On<OrderPlaced>()
            .On<OrderCancelled>();

        // The fields the board declares, so the model carries them before the code does (jasperfx#1299).
        // Once a type has members, what the code says wins; a field declared here that the code lacks is a hotspot.
        model.Shape<Order>()
            .Field("Id").OfType<OrderId>().Identity();

        model.Shape<PlaceOrder>()
            .Field("OrderId").OfType<OrderId>()
            .Field("Total").OfType<decimal>();

        model.Shape<CancelOrder>()
            .Field("OrderId").OfType<OrderId>();

        model.Shape<OrderPlaced>()
            .Field("OrderId").OfType<OrderId>();

        model.Shape<OrderSummary>()
            .Field("Id").OfType<OrderId>().Identity();
    }
}
