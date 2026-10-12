using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Tests.Partial;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// bobcat#476: an ordered set verification reports ORDER only when no pairing of expected items with
/// actual ones keeps the specification's order. Two identical expectations can each match the other's
/// actual item, and pairing them crosswise used to read as a reordering of a stream that matched
/// position by position.
/// </summary>
public class OrderedPairingTests
{
    private static readonly Guid Order = Guid.NewGuid();

    private static readonly Shipped Ups = new(Order, "UPS", "1Z1", 1m);
    private static readonly Shipped UpsAgain = new(Order, "UPS", "1Z2", 2m);
    private static readonly Delivered Delivered = new(Order, "Ann");

    private static IReadOnlyList<string> problems(object[] actual, object[] expected, SetMode mode = SetMode.Ordered)
        => ObjectSetVerification.Problems(ObjectSetVerification.Verify(actual, expected, "event", mode), "event");

    // --- objects ---

    [Fact]
    public void two_identical_type_expectations_pair_positionally()
    {
        problems([Ups, UpsAgain], [typeof(Shipped), typeof(Shipped)]).ShouldBeEmpty();
    }

    [Fact]
    public void two_identical_partial_objects_pair_positionally()
    {
        var run = ObjectSetVerification.Verify([Ups, UpsAgain],
        [
            Specify<Shipped>().With(x => x.Carrier, "UPS"),
            Specify<Shipped>().With(x => x.Carrier, "UPS")
        ], "event");

        ObjectSetVerification.Problems(run, "event").ShouldBeEmpty();
        run.Cells.ShouldNotContain(c => c.Name == SetVerificationComparer.OutOfOrderCell);
    }

    [Fact]
    public void two_identical_whole_objects_pair_positionally()
    {
        var run = ObjectSetVerification.Verify([Ups, Delivered, Ups], [Ups, Delivered, Ups], "event");

        ObjectSetVerification.Problems(run, "event").ShouldBeEmpty();
        run.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void identical_expectations_among_different_ones_pair_positionally()
    {
        problems([Ups, Delivered, UpsAgain], [typeof(Shipped), typeof(Delivered), typeof(Shipped)])
            .ShouldBeEmpty();
    }

    [Fact]
    public void a_positional_pairing_shows_each_row_its_own_actual_item()
    {
        var run = ObjectSetVerification.Verify([Ups, UpsAgain],
        [
            Specify<Shipped>().With(x => x.Carrier, "UPS"),
            Specify<Shipped>().With(x => x.Carrier, "UPS")
        ], "event");

        // The noun cell of each row carries what it was paired with; the stream matched position by position
        var nouns = run.Cells.Where(c => c.Name == "event").OrderBy(c => c.RowIndex).ToList();
        nouns.Select(c => c.RowIndex).ShouldBe([0, 1]);
        nouns.ShouldAllBe(c => c.Status == ResultStatus.success);
    }

    [Fact]
    public void a_genuine_swap_is_still_order()
    {
        problems([Ups, Delivered], [typeof(Delivered), typeof(Shipped)])
            .ShouldHaveSingleItem().ShouldStartWith("ORDER");
    }

    [Fact]
    public void a_genuine_swap_among_identical_expectations_is_still_order()
    {
        problems([Ups, Delivered, UpsAgain], [typeof(Shipped), typeof(Shipped), typeof(Delivered)])
            .ShouldHaveSingleItem().ShouldStartWith("ORDER");
    }

    [Fact]
    public void an_item_inserted_ahead_of_an_identical_one_is_extra_not_order()
    {
        problems([UpsAgain, Delivered, Ups], [typeof(Delivered), Specify<Shipped>().With(x => x.Carrier, "UPS")])
            .ShouldHaveSingleItem().ShouldStartWith("EXTRA Shipped");
    }

    [Fact]
    public void a_near_match_takes_the_item_that_keeps_the_order()
    {
        // Neither Shipped matches the expectation exactly; the one after Delivered is where it belongs
        var found = problems([UpsAgain, Delivered, Ups],
            [typeof(Delivered), Specify<Shipped>().With(x => x.Carrier, "DHL")], SetMode.Ordered);

        found.Count.ShouldBe(2);
        found[0].ShouldBe("FAIL Shipped: expected Carrier: DHL, was Carrier: UPS");
        found[1].ShouldStartWith("EXTRA Shipped");
        found[1].ShouldContain("1Z2");
    }

    [Fact]
    public void any_order_is_unchanged_by_identical_expectations()
    {
        problems([Ups, UpsAgain], [typeof(Shipped), typeof(Shipped)], SetMode.AnyOrder).ShouldBeEmpty();
        problems([Ups, Delivered], [typeof(Delivered), typeof(Shipped)], SetMode.AnyOrder).ShouldBeEmpty();
        problems([Ups, Delivered, UpsAgain], [typeof(Shipped), typeof(Delivered)], SetMode.AnyOrder)
            .ShouldHaveSingleItem().ShouldStartWith("EXTRA Shipped");
    }

    // --- table rows ---

    public record Item(string Sku, string Name, int Quantity);

    private static Dictionary<string, string> row(string sku, string name, string qty)
        => new() { ["Sku"] = sku, ["Name"] = name, ["Quantity"] = qty };

    private static TableRun compare(Item[] actual, Dictionary<string, string>[] expected, bool ordered = true)
        => SetVerificationComparer.Cells(actual, expected, ["Sku"], ordered);

    [Fact]
    public void identical_rows_pair_positionally()
    {
        var result = compare([new Item("SKU-1", "Widget", 1), new Item("SKU-1", "Widget", 1)],
            [row("SKU-1", "Widget", "1"), row("SKU-1", "Widget", "1")]);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void rows_with_the_same_key_pair_positionally()
    {
        var result = compare([new Item("SKU-1", "Widget", 1), new Item("SKU-1", "Widget", 2)],
            [row("SKU-1", "Widget", "1"), row("SKU-1", "Widget", "2")]);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void a_row_inserted_ahead_of_an_identical_one_is_extra_not_order()
    {
        var result = compare(
            [new Item("SKU-1", "Widget", 1), new Item("SKU-2", "Gadget", 2), new Item("SKU-1", "Widget", 1)],
            [row("SKU-2", "Gadget", "2"), row("SKU-1", "Widget", "1")]);

        result.Cells.ShouldNotContain(c => c.Name == SetVerificationComparer.OutOfOrderCell);
        result.Cells.Single(c => c.Name == "extra-row").Note.ShouldContain("SKU-1");
    }

    [Fact]
    public void a_genuine_row_swap_among_identical_rows_is_still_order()
    {
        var result = compare(
            [new Item("SKU-1", "Widget", 1), new Item("SKU-2", "Gadget", 2), new Item("SKU-1", "Widget", 1)],
            [row("SKU-1", "Widget", "1"), row("SKU-1", "Widget", "1"), row("SKU-2", "Gadget", "2")]);

        result.Cells.Single(c => c.Name == SetVerificationComparer.OutOfOrderCell).RowIndex.ShouldBe(2);
        result.Cells.ShouldNotContain(c => c.Name == "extra-row" || c.Name == "missing-row");
    }

    [Fact]
    public void an_unordered_table_comparison_is_unchanged()
    {
        var result = compare(
            [new Item("SKU-1", "Widget", 1), new Item("SKU-2", "Gadget", 2), new Item("SKU-1", "Widget", 1)],
            [row("SKU-2", "Gadget", "2"), row("SKU-1", "Widget", "1"), row("SKU-1", "Widget", "1")], ordered: false);

        result.Succeeded.ShouldBeTrue();
    }
}
