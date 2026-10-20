namespace Bobcat.Xunit.Samples.Application;

/// <summary>
/// The domain <c>docs/expected-objects.md</c> compares against: an order with a nested address, a
/// collection member, a dictionary member, an amount and a timestamp, so every expectation form on
/// that page has something real to disagree with.
/// </summary>
// #region placed-order
public record OrderLine(string Sku, int Quantity);

public record ShippingAddress(string City, string Country);

public record PlacedOrder(
    string Id,
    ShippingAddress ShipTo,
    List<OrderLine> Lines,
    Dictionary<string, string> Headers,
    decimal Total,
    DateTimeOffset PlacedAt);
// #endregion placed-order
