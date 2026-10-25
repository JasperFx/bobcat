namespace Bobcat.Aspire.Samples;

/// <summary>
/// Specifications against a real Postgres that the Aspire AppHost started. The assembly fixture is
/// a constructor parameter, so every spec gets the one running instance.
/// </summary>
// #region specs
[BobcatFeature("Order ledger")]
public sealed class OrderLedgerSpecs(TestInfrastructure infrastructure) : IAsyncDisposable
{
    // The system under test, handed the connection string the AppHost produced.
    private readonly OrderLedger _ledger = new(infrastructure.OrdersConnectionString);

    private OrderSteps Steps => new(_ledger);

    [BobcatSpec]
    public async Task a_customer_total_sums_their_orders()
    {
        await Steps.TheLedgerIsEmpty();
        await Steps.PlacesAnOrder("Ada", 40m);
        await Steps.PlacesAnOrder("Ada", 2.50m);

        await Steps.HasSpent("Ada", 42.50m);
    }

    [BobcatSpec]
    public async Task another_customer_orders_are_not_counted()
    {
        await Steps.TheLedgerIsEmpty();
        await Steps.PlacesAnOrder("Ada", 10m);
        await Steps.PlacesAnOrder("Grace", 99m);

        await Steps.HasSpent("Ada", 10m);
    }

    public ValueTask DisposeAsync() => _ledger.DisposeAsync();
}
// #endregion specs
