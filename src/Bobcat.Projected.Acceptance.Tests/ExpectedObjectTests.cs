using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using static Bobcat.Specifications;

namespace Bobcat.Projected.Acceptance.Tests;

public record OrderLine(string Sku, int Quantity);

public record ShippingAddress(string City, string Country);

public record PlacedOrder(string Id, ShippingAddress ShipTo, List<OrderLine> Lines, decimal Total,
    DateTimeOffset PlacedAt);

/// <summary>
/// bobcat#456, as a reader sees it: the worked examples behind <c>docs/expected-objects.md</c>. Every
/// assertion here is on the GRID a failure renders, not on the difference list — the point of the
/// issue was that a reader could not tell what had happened.
/// </summary>
public class ExpectedObjectTests
{
    public class OrderGrammar
    {
        [Then("the order matches")]
        internal void TheOrderMatches(PlacedOrder order, IPartialObject expected)
            => PropertyCells.Verify(order, expected);
    }

    private readonly OrderGrammar _grammar = new();

    private static readonly PlacedOrder TheOrder = new(
        "O-1",
        new ShippingAddress("Austin", "US"),
        [new OrderLine("ABC", 2), new OrderLine("DEF", 1)],
        30.004m,
        DateTimeOffset.UnixEpoch);

    private static CellRender cell(SpecRender render, string name)
        => render.Step("the order matches").Cells.FirstOrDefault(x => x.Name == name)
           ?? throw new ArgumentException(
               $"No '{name}' cell. Cells: {string.Join(" | ", render.Step("the order matches").Cells.Select(x => x.Name))}");

    [Fact]
    public void a_reordered_collection_member_is_not_a_disagreement_at_all()
    {
        var (recording, render) = Projected.Run("a reordered collection", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>()
                .With(x => x.Lines, new List<OrderLine> { new("DEF", 1), new("ABC", 2) })));

        recording.Steps.Single().Status.ShouldBe(ResultStatus.success);
        cell(render, "Lines").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void the_order_is_the_claim_when_the_specification_says_so()
    {
        var (_, render) = Projected.Run("a claimed order", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>()
                .With(x => x.Lines, Expect.Exactly(new OrderLine("DEF", 1), new OrderLine("ABC", 2)))));

        var lines = cell(render, "Lines");
        lines.Status.ShouldBe(ResultStatus.failed);
        lines.Expected.ShouldContain("Out of order");
    }

    [Fact]
    public void a_failure_inside_a_collection_member_names_the_item_and_the_member_that_disagreed()
    {
        var (_, render) = Projected.Run("one wrong quantity", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>()
                .With(x => x.Lines, new List<OrderLine> { new("ABC", 2), new("DEF", 9) })));

        var lines = cell(render, "Lines");
        lines.Status.ShouldBe(ResultStatus.failed);

        // The member that disagreed, at the position the reader will find the item in -- not the
        // whole collection rendered identically on both sides, which is what bobcat#456 opened on
        lines.Expected.ShouldBe("[1].Quantity: 9");
        lines.Actual.ShouldBe("[1].Quantity: 1");
    }

    [Fact]
    public void an_item_nobody_expected_says_it_is_extra()
    {
        var (_, render) = Projected.Run("an extra line", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>()
                .With(x => x.Lines, Expect.Containing(Specify<OrderLine>().With(l => l.Sku, "ABC")))
                .With(x => x.Lines, Expect.InAnyOrder(Specify<OrderLine>().With(l => l.Sku, "ABC")))));

        var lines = cell(render, "Lines");
        lines.Status.ShouldBe(ResultStatus.failed);
        lines.Actual.ShouldContain("EXTRA: nothing expected a OrderLine here");
    }

    [Fact]
    public void an_expectation_nothing_satisfied_says_it_is_missing()
    {
        var (_, render) = Projected.Run("a missing line", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>()
                .With(x => x.Lines, Expect.Containing(
                    Specify<OrderLine>().With(l => l.Sku, "ABC"),
                    Specify<OrderLine>().With(l => l.Sku, "DEF"),
                    Specify<OrderLine>().With(l => l.Sku, "GHI")))));

        var lines = cell(render, "Lines");
        lines.Status.ShouldBe(ResultStatus.failed);
        lines.Expected.ShouldContain("MISSING: no OrderLine like this");
    }

    [Fact]
    public void the_right_item_with_a_wrong_member_is_that_member_and_not_a_missing_beside_an_extra()
    {
        // The near-match rule a top-level set verification has always had: "the right event with the
        // wrong amount" deserves a FAIL naming the member, not a MISSING row beside an EXTRA one
        var (_, render) = Projected.Run("the wrong sku", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>()
                .With(x => x.Lines, Expect.InAnyOrder(
                    Specify<OrderLine>().With(l => l.Sku, "ABC"),
                    Specify<OrderLine>().With(l => l.Sku, "XYZ")))));

        var lines = cell(render, "Lines");
        lines.Status.ShouldBe(ResultStatus.failed);
        lines.Expected.ShouldBe("[1].Sku: XYZ");
        lines.Actual.ShouldBe("[1].Sku: DEF");
    }

    [Fact]
    public void a_nested_member_reads_as_its_path()
    {
        var (_, render) = Projected.Run("a nested member", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>().With(x => x.ShipTo.City, "Dallas")));

        var city = cell(render, "ShipTo.City");
        city.Status.ShouldBe(ResultStatus.failed);
        city.Expected.ShouldBe("Dallas");
        city.Actual.ShouldBe("Austin");
    }

    [Fact]
    public void an_unspecified_member_is_neither_judged_nor_shown()
    {
        var (_, render) = Projected.Run("only what it names", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>().With(x => x.Id, "O-1")));

        render.Step("the order matches").Cells.Select(x => x.Name).ShouldBe(["Id"]);
    }

    [Fact]
    public void a_whole_object_expectation_relaxes_the_members_it_names()
    {
        var actual = TheOrder with { Total = 30.004m, PlacedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
        var expected = TheOrder with { Total = 30m, PlacedAt = DateTimeOffset.UnixEpoch };

        PartialMatching.Differences(actual, Expect.Value(expected)
                .Approximately(x => x.Total, 0.01m)
                .Within(x => x.PlacedAt, TimeSpan.FromSeconds(2)))
            .ShouldBeEmpty();

        var differences = PartialMatching.Differences(actual, Expect.Value(expected)
            .Approximately(x => x.Total, 0.0001m)
            .Within(x => x.PlacedAt, TimeSpan.FromMilliseconds(10)));

        differences.Select(x => x.Path).ShouldBe(["Total", "PlacedAt"], ignoreOrder: true);
        differences.Single(x => x.Path == "Total").Note.ShouldBe("±0.0001");
    }

    [Fact]
    public void a_child_collection_may_be_written_as_a_table()
    {
        var (recording, _) = Projected.Run("a child collection as a table", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>().With(x => x.Lines, Expect.Rows("""
                | Sku | Quantity |
                | DEF | 1        |
                | ABC | 2        |
                """))));

        recording.Steps.Single().Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void the_whole_scenario_still_reads_as_a_specification()
    {
        var (_, render) = Projected.Run("a failure a reader can follow", () =>
            _grammar.TheOrderMatches(TheOrder, Specify<PlacedOrder>()
                .With(x => x.ShipTo.City, "Dallas")
                .With(x => x.Lines, new List<OrderLine> { new("ABC", 2), new("DEF", 9) })));

        var text = string.Join(Environment.NewLine, PlainSpecText.Lines(render));

        text.ShouldContain("the order matches");
        text.ShouldContain("ShipTo.City");
        text.ShouldContain("[1].Quantity");
    }
}
