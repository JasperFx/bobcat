using Bobcat.Runtime;
using Bobcat.Xunit.Samples.Application;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// Two steps that take an expected object, the way every <c>Then…</c> in the Critter Stack vocabulary
/// does, over core's own engines: a partial object through <see cref="PropertyCells"/>, a whole or
/// relaxed object through <see cref="ObjectSetVerification"/>. What <c>docs/expected-objects.md</c>
/// renders comes from these.
/// </summary>
// #region order-grammar
public class OrderGrammar
{
    public PlacedOrder TheOrder { get; } = new(
        "O-1",
        new ShippingAddress("Austin", "US"),
        [new OrderLine("ABC", 2), new OrderLine("DEF", 1)],
        new Dictionary<string, string> { ["accept"] = "json", ["auth"] = "token" },
        30.004m,
        new DateTimeOffset(2026, 10, 20, 9, 0, 1, TimeSpan.Zero));

    /// <summary>A partial object: judged, and shown, on only the members it names.</summary>
    [Then("the order matches")]
    internal void TheOrderMatches(IPartialObject expected) => PropertyCells.Verify(TheOrder, expected);

    /// <summary>A whole object, or an <c>Expect.Value(…)</c>: every member, structurally.</summary>
    [Then("the order is")]
    internal void TheOrderIs(object expected)
        => ObjectSetVerification.Verify([TheOrder], [expected], "order", SetMode.AnyOrder).Report();
}
// #endregion order-grammar
