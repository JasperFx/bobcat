using Bobcat;
using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.Partial;

public class PartialObjectTablesTests
{
    [Fact]
    public void a_horizontal_table_is_one_partial_object_per_row()
    {
        StepTable table = """
                          | Customer | Total |
                          | Ann      | 12.50 |
                          | Bob      | 3     |
                          """;

        var orders = PartialObjects.FromTable(typeof(PositionalOrder), table)
            .Select(x => (PositionalOrder)x.Build())
            .ToList();

        orders.Select(x => x.Customer).ShouldBe(["Ann", "Bob"]);
        orders.Select(x => x.Total).ShouldBe([12.50m, 3m]);
    }

    [Fact]
    public void a_vertical_table_is_one_partial_object()
    {
        StepTable table = """
                          | field    | value |
                          | Customer | Ann   |
                          | Lines    | 4     |
                          """;

        var partial = PartialObjects.FromTable(typeof(PositionalOrder), table).ShouldHaveSingleItem();
        var order = (PositionalOrder)partial.Build();

        order.Customer.ShouldBe("Ann");
        order.Lines.ShouldBe(4);
    }

    [Fact]
    public void the_vertical_header_ignores_case()
    {
        PartialObjects.IsVertical("""
                                  | Field | VALUE |
                                  | Lines | 4     |
                                  """).ShouldBeTrue();
    }

    [Fact]
    public void a_vertical_table_naming_a_member_twice_is_refused()
    {
        StepTable table = """
                          | field    | value |
                          | Customer | Ann   |
                          | customer | Bob   |
                          """;

        Should.Throw<SpecCriticalException>(() => PartialObjects.FromTable(typeof(PositionalOrder), table))
            .Message.ShouldContain("names 'customer' twice");
    }

    [Fact]
    public void a_blank_cell_is_not_specified()
    {
        StepTable table = """
                          | Customer | Lines |
                          | Ann      |       |
                          """;

        var partial = PartialObjects.FromTable(typeof(PositionalOrder), table).Single();

        partial.Values.Select(x => x.Path).ShouldBe(["Customer"]);
        ((PositionalOrder)partial.Build()).Lines.ShouldBe(0);
    }

    [Fact]
    public void empty_and_null_tokens()
    {
        StepTable table = """
                          | Note  | Count |
                          | EMPTY | NULL  |
                          """;

        var built = (Nullables)PartialObjects.FromTable(typeof(Nullables), table).Single().Build();

        built.Note.ShouldBe("");
        built.Count.ShouldBeNull();
    }

    [Fact]
    public void a_relative_time_reads_against_the_bobcat_clock()
    {
        BobcatClock.Set(new ControllableTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)));
        try
        {
            StepTable table = """
                              | At    |
                              | TODAY |
                              """;

            var built = (Temporal)PartialObjects.FromTable(typeof(Temporal), table).Single().Build();
            built.At.Date.ShouldBe(new DateTime(2026, 10, 7));
        }
        finally
        {
            BobcatClock.Reset();
        }
    }

    [Fact]
    public void temporal_values_from_cells()
    {
        StepTable table = """
                          | field    | value                     |
                          | At       | 2026-10-07T12:00:00Z      |
                          | On       | 2026-10-07                |
                          | Time     | 13:45                     |
                          | Stamp    | 2026-10-07T12:00:00+02:00 |
                          | Duration | 01:30:00                  |
                          """;

        var built = (Temporal)PartialObjects.FromTable(typeof(Temporal), table).Single().Build();

        built.On.ShouldBe(new DateOnly(2026, 10, 7));
        built.Time.ShouldBe(new TimeOnly(13, 45));
        built.Stamp.Offset.ShouldBe(TimeSpan.FromHours(2));
        built.Duration.ShouldBe(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void guid_and_enum_cells()
    {
        var id = Guid.NewGuid();
        StepTable table = $"""
                           | OrderId | Status  |
                           | {id}    | Shipped |
                           """;

        var built = (PositionalOrder)PartialObjects.FromTable(typeof(PositionalOrder), table).Single().Build();

        built.OrderId.ShouldBe(id);
        built.Status.ShouldBe(OrderStatus.Shipped);
    }

    [Fact]
    public void an_unreadable_cell_names_the_member()
    {
        StepTable table = """
                          | Lines |
                          | many  |
                          """;

        Should.Throw<BadCellException>(() => PartialObjects.FromTable(typeof(PositionalOrder), table).Single().Build())
            .Message.ShouldStartWith("'Lines': ");
    }

    [Fact]
    public void an_enum_cell_that_is_not_a_member_lists_the_choices()
    {
        StepTable table = """
                          | Status    |
                          | Cancelled |
                          """;

        Should.Throw<BadCellException>(() => PartialObjects.FromTable(typeof(PositionalOrder), table).Single().Build())
            .Message.ShouldContain("Placed, Shipped");
    }

    [Fact]
    public void collections_from_a_comma_separated_cell()
    {
        StepTable table = """
                          | Skus  | Quantities | Codes |
                          | A, B  | 1,2,3      | EMPTY |
                          """;

        var basket = (Basket)PartialObjects.FromTable(typeof(Basket), table).Single().Build();

        basket.Skus.ShouldBe(["A", "B"]);
        basket.Quantities.ShouldBe([1, 2, 3]);
        basket.Codes.ShouldBeEmpty();
    }

    [Fact]
    public void a_dotted_column_specifies_a_nested_member()
    {
        StepTable table = """
                          | Name | Address.City |
                          | Ann  | Austin       |
                          """;

        var customer = (Customer)PartialObjects.FromTable(typeof(Customer), table).Single().Build();

        customer.Name.ShouldBe("Ann");
        customer.Address.City.ShouldBe("Austin");
    }

    [Fact]
    public void a_header_titled_column()
    {
        StepTable table = """
                          | Order # | Quantity |
                          | A-1     | 2        |
                          """;

        var titled = (Titled)PartialObjects.FromTable(typeof(Titled), table).Single().Build();

        titled.OrderNumber.ShouldBe("A-1");
        titled.Quantity.ShouldBe(2);
    }

    [Fact]
    public void a_column_matches_a_member_ignoring_case()
    {
        StepTable table = """
                          | customer | TOTAL |
                          | Ann      | 2     |
                          """;

        var order = (PositionalOrder)PartialObjects.FromTable(typeof(PositionalOrder), table).Single().Build();

        order.Customer.ShouldBe("Ann");
        order.Total.ShouldBe(2m);
    }

    [Fact]
    public void an_unknown_column_is_refused()
    {
        StepTable table = """
                          | Customer | Totl |
                          | Ann      | 2    |
                          """;

        Should.Throw<SpecCriticalException>(() => PartialObjects.FromTable(typeof(PositionalOrder), table).Single().Build())
            .Message.ShouldContain("[Totl] matches nothing on 'PositionalOrder'");
    }

    [Fact]
    public void a_table_description_names_only_the_specified_cells()
    {
        StepTable table = """
                          | Customer | Lines |
                          | Ann      |       |
                          """;

        PartialObjects.FromTable(typeof(PositionalOrder), table).Single().ToString()
            .ShouldBe("PositionalOrder(Customer: Ann)");
    }

    [Fact]
    public void a_quoted_cell_is_literal_text()
    {
        StepTable table = """
                          | Note   |
                          | "NULL" |
                          """;

        ((Nullables)PartialObjects.FromTable(typeof(Nullables), table).Single().Build()).Note.ShouldBe("NULL");
    }
}
