using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Bobcat.Runtime;
using Shouldly;
using static Bobcat.Tests.Verification.CellValuesConversionTests;

namespace Bobcat.Tests.Verification;

/// <summary>
/// A comma-delimited cell read as an array or list (bobcat#543): every common collection shape, each
/// item through the same conversion as a scalar, the tokens, and the one rule partial objects share.
/// </summary>
public class CellValuesListTests
{
    [Fact]
    public void an_array_reads_from_a_comma_list_with_each_item_trimmed()
    {
        CellValues.Read<int[]>("1, 2 ,3").ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void every_common_collection_shape_reads_the_same_list()
    {
        CellValues.Read<List<string>>("a, b").ShouldBe(["a", "b"]);
        CellValues.Read<IReadOnlyList<string>>("a, b").ShouldBe(["a", "b"]);
        CellValues.Read<IEnumerable<string>>("a, b").ShouldBe(["a", "b"]);
        CellValues.Read<IList<string>>("a, b").ShouldBe(["a", "b"]);
        CellValues.Read<ICollection<string>>("a, b").ShouldBe(["a", "b"]);
        CellValues.Read<IReadOnlyCollection<string>>("a, b").ShouldBe(["a", "b"]);
        CellValues.Read<HashSet<string>>("a, b, a").ShouldBe(["a", "b"], ignoreOrder: true);
        CellValues.Read<ISet<string>>("a, b").ShouldBe(["a", "b"], ignoreOrder: true);
        CellValues.Read<IReadOnlySet<string>>("a, b").ShouldBe(["a", "b"], ignoreOrder: true);
    }

    [Fact]
    public void each_item_goes_through_the_scalar_conversion_so_a_list_of_strong_typed_ids_works()
    {
        var ids = CellValues.Read<List<OrderId>>(
            "11111111-1111-1111-1111-111111111111, 22222222-2222-2222-2222-222222222222");

        ids.ShouldBe([
            new OrderId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new OrderId(Guid.Parse("22222222-2222-2222-2222-222222222222"))
        ]);
    }

    [Fact]
    public void items_honour_a_registration_enums_and_relative_times()
    {
        CellValues.Register(text => new Badge("[" + text + "]"));
        try
        {
            CellValues.Read<Badge[]>("gold, silver").ShouldBe([new Badge("[gold]"), new Badge("[silver]")]);
        }
        finally
        {
            CellValues.Unregister<Badge>();
        }

        CellValues.Read<DayOfWeek[]>("monday, Friday").ShouldBe([DayOfWeek.Monday, DayOfWeek.Friday]);

        var today = CellValues.Read<DateOnly>("TODAY");
        CellValues.Read<DateOnly[]>("TODAY, TODAY+1").ShouldBe([today, today.AddDays(1)]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("EMPTY")]
    [InlineData("empty")]
    public void a_blank_cell_or_EMPTY_is_an_empty_collection(string cell)
    {
        CellValues.Read<int[]>(cell).ShouldBeEmpty();
        CellValues.Read<List<string>>(cell).ShouldBeEmpty();
    }

    [Fact]
    public void NULL_is_null()
    {
        CellValues.Read<int[]>("NULL").ShouldBeNull();
        CellValues.Read<IReadOnlyList<string>>("null").ShouldBeNull();
    }

    [Fact]
    public void a_comma_inside_quotes_belongs_to_its_item_and_quotes_keep_their_literal_meaning()
    {
        CellValues.Read<string[]>("\"Smith, Jane\", Doe").ShouldBe(["Smith, Jane", "Doe"]);
        CellValues.Read<string[]>("\"NULL\", x").ShouldBe(["NULL", "x"]);
        CellValues.Read<string?[]>("NULL, x").ShouldBe([null, "x"]);
    }

    [Fact]
    public void a_bad_item_is_a_bad_cell_naming_the_item_and_the_whole_cell()
    {
        var ex = Should.Throw<BadCellException>(() => CellValues.Read<int[]>("1, two, 3"));

        ex.Message.ShouldContain("'1, two, 3'");
        ex.Message.ShouldContain("item 2 of 3");
        ex.Message.ShouldContain("'two'");
    }

    [Fact]
    public void a_registration_for_the_collection_type_itself_wins()
    {
        CellValues.Register<Shade[]>(text => text.Split('|').Select(x => new Shade(x)).ToArray());
        try
        {
            CellValues.Read<Shade[]>("a|b").ShouldBe([new Shade("a"), new Shade("b")]);
        }
        finally
        {
            CellValues.Unregister<Shade[]>();
        }
    }

    [Fact]
    public void a_string_is_not_a_collection_of_chars()
    {
        CellValues.Read<string>("a, b").ShouldBe("a, b");
    }

    [Fact]
    public void partial_objects_read_a_collection_member_by_the_same_rule()
    {
        var built = TableRunner.BuildRows<Tagged>("""
            | Name | Tags                 |
            | one  | "a, b", c            |
            | two  | EMPTY                |
            """);

        built[0].Tags.ShouldBe(["a, b", "c"]);
        built[1].Tags.ShouldBeEmpty();
    }

    [Fact]
    public void an_expected_list_is_compared_by_its_items_in_order()
    {
        var match = CellCheck.For("ids", new List<int> { 1, 2, 3 }, "1, 2, 3");
        match.Status.ShouldBe(ResultStatus.success);
        match.Expected.ShouldBe("1, 2, 3");
        match.Actual.ShouldBe("1, 2, 3");

        var reordered = CellCheck.For("ids", new[] { 3, 2, 1 }, "1, 2, 3");
        reordered.Status.ShouldBe(ResultStatus.failed);
        reordered.Actual.ShouldBe("3, 2, 1");
    }

    [Fact]
    public void an_expected_set_ignores_order()
    {
        CellCheck.For("tags", new HashSet<string> { "b", "a" }, "a, b").Status.ShouldBe(ResultStatus.success);
        CellCheck.For("tags", new HashSet<string> { "b", "a" }, "a, c").Status.ShouldBe(ResultStatus.failed);
    }

    public sealed record Tagged(string Name, IReadOnlyList<string> Tags);
}
