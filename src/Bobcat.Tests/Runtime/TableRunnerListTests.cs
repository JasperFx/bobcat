using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;
using static Bobcat.Tests.Verification.CellValuesConversionTests;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// A row-method parameter that is an array or list binds from a comma-delimited cell (bobcat#543),
/// and an out list is judged item by item.
/// </summary>
public class TableRunnerListTests
{
    public class Orders
    {
        public List<(string Customer, int[] Quantities, IReadOnlyList<OrderId> Ids)> Seen { get; } = [];

        public void Place(string customer, int[] quantities, IReadOnlyList<OrderId> ids)
            => Seen.Add((customer, quantities, ids));

        public void Split(string text, out List<string> parts)
            => parts = text.Split('-').ToList();
    }

    private readonly Orders _target = new();

    [Fact]
    public async Task an_array_or_list_parameter_binds_from_a_comma_list()
    {
        var run = await TableRunner.Run(_target, nameof(Orders.Place), """
            | customer | quantities | ids                                                                        |
            | ann      | 1, 2, 3    | 11111111-1111-1111-1111-111111111111, 22222222-2222-2222-2222-222222222222 |
            | bob      | EMPTY      |                                                                            |
            """);

        run.Succeeded.ShouldBeTrue();
        _target.Seen.Count.ShouldBe(2);

        _target.Seen[0].Quantities.ShouldBe([1, 2, 3]);
        _target.Seen[0].Ids.ShouldBe([
            new OrderId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new OrderId(Guid.Parse("22222222-2222-2222-2222-222222222222"))
        ]);

        _target.Seen[1].Quantities.ShouldBeEmpty();
        _target.Seen[1].Ids.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_bad_item_is_a_row_error_naming_the_item()
    {
        var run = await TableRunner.Run(_target, nameof(Orders.Place), """
            | customer | quantities | ids   |
            | ann      | 1, x       | EMPTY |
            """);

        run.Succeeded.ShouldBeFalse();
        var error = run.Cells.Single(c => c.Name == DecisionTableComparer.RowErrorCell);
        error.DisplayText.ShouldContain("item 2 of 2");
    }

    [Fact]
    public async Task an_out_list_is_an_output_judged_by_its_items()
    {
        var run = await TableRunner.Run(_target, nameof(Orders.Split), """
            | text  | parts   |
            | a-b-c | a, b, c |
            | a-b   | b, a    |
            """);

        run.Cells.Single(c => c.RowIndex == 0 && c.Name == "parts").Status.ShouldBe(ResultStatus.success);

        var wrong = run.Cells.Single(c => c.RowIndex == 1 && c.Name == "parts");
        wrong.Status.ShouldBe(ResultStatus.failed);
        wrong.Expected.ShouldBe("b, a");
        wrong.Actual.ShouldBe("a, b");
    }
}
