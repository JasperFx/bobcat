using Bobcat;
using Bobcat.Engine;
using Bobcat.Partial;
using Bobcat.Runtime;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Partial;

/// <summary>bobcat#678: a collection member reads as its items, never as its CLR type.</summary>
public class CollectionMemberRenderingTests
{
    public record Product(string Name, List<string> Category);

    public record Transfer(string To, decimal Amount);

    public record Wallet(string Owner, List<Transfer> Transfers);

    [Fact]
    public void a_specified_collection_member_describes_its_items()
    {
        Specify<Product>().With(x => x.Category, ["New Cat"]).ToString()
            .ShouldBe("Product(Category: [\"New Cat\"])");
    }

    [Fact]
    public void a_matched_collection_member_cell_shows_its_items()
    {
        var cell = PropertyCells.Verify(new Product("A", ["New Cat"]),
            Specify<Product>().With(x => x.Category, ["New Cat"])).Cells.Single();

        cell.Status.ShouldBe(ResultStatus.success);
        cell.Expected.ShouldBe("[\"New Cat\"]");
        cell.Actual.ShouldBe("[\"New Cat\"]");
        cell.DisplayText.ShouldBe("[\"New Cat\"]");
    }

    [Fact]
    public void a_check_on_a_collection_member_shows_the_check_and_the_items()
    {
        var check = Specify<Wallet>(x => x.Transfers.ShouldHaveSingleItem(null));
        check.ToString().ShouldBe("Wallet(Transfers: should have single item)");

        var held = PropertyCells.Verify(new Wallet("w", [new Transfer("b", 1m)]), check).Cells.Single();
        held.Status.ShouldBe(ResultStatus.success);
        held.Expected.ShouldBe("should have single item");
        held.Actual.ShouldBe("[Transfer(To: \"b\", Amount: 1)]");
        held.DisplayText.ShouldBe("should have single item");

        var failed = PropertyCells.Verify(new Wallet("w", []), check).Cells.Single();
        failed.Status.ShouldBe(ResultStatus.failed);
        failed.Expected.ShouldBe("should have single item");
        failed.Actual.ShouldBe("[]");
    }

    [Fact]
    public void a_collection_formats_as_its_items_but_a_type_that_renders_itself_keeps_its_own_text()
    {
        ScenarioValues.Format(new List<string> { "a", "b" }).ShouldBe("[\"a\", \"b\"]");
        ScenarioValues.Format(new[] { 1, 2 }).ShouldBe("[1, 2]");
        ScenarioValues.Format("text").ShouldBe("text");
        ScenarioValues.Format(new SelfRendering()).ShouldBe("mine");
    }

    private sealed class SelfRendering : List<int>
    {
        public override string ToString() => "mine";
    }
}
