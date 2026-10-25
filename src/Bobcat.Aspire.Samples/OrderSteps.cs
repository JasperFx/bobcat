namespace Bobcat.Aspire.Samples;

/// <summary>Step helpers over <see cref="OrderLedger"/>: each call renders as one step.</summary>
public sealed class OrderSteps(OrderLedger ledger)
{
    [Given("the ledger is empty")]
    internal Task TheLedgerIsEmpty() => ledger.Reset();

    [Given("{customer} places an order for {total}")]
    internal Task PlacesAnOrder(string customer, decimal total) => ledger.Record(customer, total);

    [Then("{customer} has spent {total}")]
    internal async Task HasSpent(string customer, decimal total)
        => SpecAssert.Check("total", await ledger.TotalFor(customer), total);
}
