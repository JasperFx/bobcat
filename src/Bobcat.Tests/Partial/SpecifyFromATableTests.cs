using Bobcat;
using Bobcat.Engine;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Partial;

public class SpecifyFromATableTests
{
    private static readonly Guid Order = Guid.Parse("0199a3a0-0000-7000-8000-000000000001");

    [Fact]
    public void a_property_value_table_specifies_one_object()
    {
        var order = Specify<PositionalOrder>($$"""
            | Property | Value     |
            | OrderId  | {{Order}} |
            | Customer | Ann       |
            | Total    | 12.50     |
            | Lines    | 3         |
            """).Build();

        order.OrderId.ShouldBe(Order);
        order.Customer.ShouldBe("Ann");
        order.Total.ShouldBe(12.50m);
        order.Lines.ShouldBe(3);
    }

    [Fact]
    public void the_members_as_headers_over_one_row_specify_one_object_too()
    {
        var order = Specify<PositionalOrder>("""
            | Customer | Total |
            | Ann      | 12.50 |
            """).Build();

        order.Customer.ShouldBe("Ann");
        order.Total.ShouldBe(12.50m);
    }

    [Fact]
    public void a_table_matches_like_a_with_chain()
    {
        var actual = new Shipped(Order, "UPS", "1Z", 5m);

        PartialMatching.Differences(actual, Specify<Shipped>("""
            | Property | Value |
            | Carrier  | UPS   |
            | Weight   | 5     |
            """)).ShouldBeEmpty();

        PartialMatching.Differences(actual, Specify<Shipped>("""
            | Property | Value |
            | Carrier  | FedEx |
            """)).ShouldNotBeEmpty();
    }

    [Fact]
    public void a_blank_cell_is_not_specified_and_with_still_chains_after_a_table()
    {
        var specified = Specify<PositionalOrder>("""
            | Property | Value |
            | Customer |       |
            """).With(x => x.Lines, 2);

        specified.Values.Select(x => x.Path).ShouldBe(["Lines"]);
    }

    [Fact]
    public void a_table_of_several_rows_is_refused_because_it_describes_several_objects()
    {
        Should.Throw<SpecCriticalException>(() => Specify<PositionalOrder>("""
            | Customer |
            | Ann      |
            | Bob      |
            """)).Message.ShouldContain("has 2 rows");
    }
}
