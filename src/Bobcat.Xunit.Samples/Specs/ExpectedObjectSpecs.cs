using Bobcat.Xunit.Samples.Application;
using Bobcat.Xunit.Samples.Grammars;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The end-to-end examples behind <c>docs/expected-objects.md</c>: each expectation form on that
/// page, once holding and once disagreeing, so the page shows what a reader actually gets. The
/// order under test is <see cref="OrderGrammar.TheOrder"/>: O-1, shipping to Austin, lines ABC×2
/// and DEF×1, headers accept and auth, total 30.004, placed at 09:00:01 on 2026-10-20.
/// </summary>
[BobcatFeature("Expected objects"), BobcatScenario]
public class ExpectedObjectSpecs
{
    private readonly OrderGrammar _orders = new();

    // ---- a partial object ----------------------------------------------------------------------

    // #region partial-holds
    [Fact]
    public void a_partial_object_names_only_what_it_is_about()
        => _orders.TheOrderMatches(Specify<PlacedOrder>()
            .With(x => x.ShipTo.City, "Austin")
            .With(x => x.Total, 30.004m));
    // #endregion partial-holds

    // #region partial-disagrees
    [Fact]
    public void a_partial_object_with_a_wrong_nested_member()
        => _orders.TheOrderMatches(Specify<PlacedOrder>()
            .With(x => x.ShipTo.City, "Dallas")
            .With(x => x.Total, 30.004m));
    // #endregion partial-disagrees

    // ---- a whole object, and Expect.Value --------------------------------------------------------

    // #region whole-object-disagrees
    [Fact]
    public void a_whole_object_is_judged_member_by_member()
        => _orders.TheOrderIs(new PlacedOrder(
            "O-1",
            new ShippingAddress("Dallas", "US"),
            [new OrderLine("ABC", 2), new OrderLine("DEF", 9)],
            new Dictionary<string, string> { ["accept"] = "json", ["auth"] = "token" },
            30.004m,
            new DateTimeOffset(2026, 10, 20, 9, 0, 1, TimeSpan.Zero)));
    // #endregion whole-object-disagrees

    // #region expect-value-holds
    [Fact]
    public void an_expected_value_relaxes_the_members_it_names()
        => _orders.TheOrderIs(Expect.Value(new PlacedOrder(
                "O-2",
                new ShippingAddress("Austin", "US"),
                [new OrderLine("ABC", 2), new OrderLine("DEF", 1)],
                new Dictionary<string, string> { ["accept"] = "json", ["auth"] = "token" },
                30m,
                new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)))
            .Ignoring(x => x.Id)                               // shown, never judged
            .Approximately(x => x.Total, 0.01m)                // within an absolute amount
            .Within(x => x.PlacedAt, TimeSpan.FromSeconds(2))  // within a window of time
            .InOrder(x => x.Lines));                           // this collection's order is the claim
    // #endregion expect-value-holds

    // ---- a collection member -------------------------------------------------------------------

    // #region collection-reordered-holds
    [Fact]
    public void a_reordered_collection_member_is_not_a_disagreement()
        => _orders.TheOrderMatches(Specify<PlacedOrder>()
            .With(x => x.Lines, new List<OrderLine> { new("DEF", 1), new("ABC", 2) }));
    // #endregion collection-reordered-holds

    // #region collection-exactly-disagrees
    [Fact]
    public void the_order_is_the_claim_when_the_specification_says_so()
        => _orders.TheOrderMatches(Specify<PlacedOrder>()
            .With(x => x.Lines, Expect.Exactly(new OrderLine("DEF", 1), new OrderLine("ABC", 2))));
    // #endregion collection-exactly-disagrees

    // #region collection-wrong-member
    [Fact]
    public void the_right_item_with_a_wrong_member_is_that_member()
        => _orders.TheOrderMatches(Specify<PlacedOrder>()
            .With(x => x.Lines, new List<OrderLine> { new("ABC", 2), new("DEF", 9) }));
    // #endregion collection-wrong-member

    // #region collection-extra-and-missing
    [Fact]
    public void an_item_nobody_expected_is_extra()
        => _orders.TheOrderMatches(Specify<PlacedOrder>()
            .With(x => x.Lines, Expect.InAnyOrder(Specify<OrderLine>().With(l => l.Sku, "ABC"))));

    [Fact]
    public void an_expectation_nothing_satisfied_is_missing()
        => _orders.TheOrderMatches(Specify<PlacedOrder>()
            .With(x => x.Lines, Expect.Containing(
                Specify<OrderLine>().With(l => l.Sku, "ABC"),
                Specify<OrderLine>().With(l => l.Sku, "DEF"),
                Specify<OrderLine>().With(l => l.Sku, "GHI"))));
    // #endregion collection-extra-and-missing

    // #region collection-as-table
    [Fact]
    public void a_child_collection_may_be_written_as_a_table()
        => _orders.TheOrderMatches(Specify<PlacedOrder>().With(x => x.Lines, Expect.Rows("""
            | Sku | Quantity |
            | DEF | 1        |
            | ABC | 2        |
            """)));
    // #endregion collection-as-table

    // ---- a dictionary member -------------------------------------------------------------------

    // #region dictionary-keyed
    [Fact]
    public void a_dictionary_member_is_compared_by_key()
        => _orders.TheOrderMatches(Specify<PlacedOrder>()
            .With(x => x.Headers, new Dictionary<string, string> { ["auth"] = "token", ["trace"] = "abc" }));
    // #endregion dictionary-keyed

    // ---- member checks -------------------------------------------------------------------------

    // #region member-checks-hold
    [Fact]
    public void a_member_check_is_a_claim_that_is_not_equality()
        => _orders.TheOrderMatches(Specify<PlacedOrder>(
            x => x.Total.ShouldBeGreaterThan(10m),
            x => x.Lines.ShouldNotBeEmpty()));
    // #endregion member-checks-hold

    // #region member-checks-disagree
    [Fact]
    public void a_member_check_that_disagrees_reports_the_assertions_own_message()
        => _orders.TheOrderMatches(Specify<PlacedOrder>(
            x => x.Total.ShouldBeGreaterThan(100m),
            x => x.Lines.ShouldNotBeEmpty()));
    // #endregion member-checks-disagree
}
