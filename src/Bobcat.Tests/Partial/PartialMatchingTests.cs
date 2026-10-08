using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Tests.FSharpTypes;
using Microsoft.FSharp.Core;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Partial;

public record Shipped(Guid OrderId, string Carrier, string TrackingNumber, decimal Weight);

public record Delivered(Guid OrderId, string SignedBy);

public record Cancelled(Guid OrderId, string Reason);

public class PartialMatchingTests
{
    private static readonly Guid Order = Guid.NewGuid();

    [Fact]
    public void a_partial_object_judges_only_the_members_it_names()
    {
        var actual = new Shipped(Order, "UPS", "1Z999", 2.5m);

        PartialMatching.Differences(actual, Specify<Shipped>().With(x => x.Carrier, "UPS")).ShouldBeEmpty();
        PartialMatching.Differences(actual, Specify<Shipped>().With(x => x.Carrier, "FedEx"))
            .ShouldHaveSingleItem().Path.ShouldBe("Carrier");
    }

    [Fact]
    public void a_table_row_judges_its_cells_with_the_cell_rules()
    {
        var actual = new Shipped(Order, "UPS", "1Z999", 2.5m);
        StepTable table = """
                          | Carrier | Weight |
                          | UPS     | 2.50   |
                          """;

        PartialMatching.Differences(actual, PartialObjects.FromTable(typeof(Shipped), table).Single()).ShouldBeEmpty();
    }

    [Fact]
    public void a_typed_value_of_a_different_numeric_type_agrees()
    {
        PartialMatching.Differences(new Shipped(Order, "UPS", "1Z", 5m),
            Specify<Shipped>().With(nameof(Shipped.Weight), 5)).ShouldBeEmpty();
    }

    [Fact]
    public void a_whole_object_judges_every_member()
    {
        var actual = new Shipped(Order, "UPS", "1Z999", 2.5m);

        PartialMatching.Differences(actual, actual with { Weight = 3m })
            .ShouldHaveSingleItem().Path.ShouldBe("Weight");
    }

    [Fact]
    public void an_expected_value_ignores_what_it_is_told_to()
    {
        var actual = new Shipped(Order, "UPS", "1Z999", 2.5m);

        PartialMatching.Differences(actual, Expect.Value(actual with { TrackingNumber = "?" }).Ignoring(x => x.TrackingNumber))
            .ShouldBeEmpty();
    }

    [Fact]
    public void an_unknown_member_in_a_partial_expectation_is_a_spec_defect()
    {
        Should.Throw<SpecCriticalException>(() =>
                PartialMatching.Differences(new Shipped(Order, "UPS", "1Z", 1m), Specify<Shipped>().With("Carier", "UPS")))
            .Message.ShouldContain("no member 'Carier'");
    }

    [Fact]
    public void fsharp_options_and_wrappers_compare_by_what_they_hold()
    {
        var id = Guid.NewGuid();
        var actual = Specify<ShipmentConfirmed>()
            .With(x => x.Carrier, FSharpOption<string>.Some("UPS"))
            .With(nameof(ShipmentConfirmed.OrderId), id)
            .Build();

        PartialMatching.Differences(actual, Specify<ShipmentConfirmed>()
            .With(nameof(ShipmentConfirmed.Carrier), "UPS")
            .With(nameof(ShipmentConfirmed.OrderId), id)).ShouldBeEmpty();

        StepTable table = $"""
                           | Carrier | OrderId |
                           | UPS     | {id}    |
                           """;
        PartialMatching.Differences(actual, PartialObjects.FromTable(typeof(ShipmentConfirmed), table).Single()).ShouldBeEmpty();
    }

    [Fact]
    public void the_actual_is_described_by_the_members_the_expectation_names()
    {
        var actual = new Shipped(Order, "UPS", "1Z999", 2.5m);

        PartialMatching.DescribeActual(actual, Specify<Shipped>().With(x => x.TrackingNumber, "1Z999"))
            .ShouldBe("TrackingNumber: 1Z999");
        PartialMatching.DescribeExpected(Specify<Shipped>().With(x => x.TrackingNumber, "1Z999"))
            .ShouldBe("TrackingNumber: 1Z999");
    }

    [Fact]
    public void a_single_object_grid_shows_only_the_specified_columns()
    {
        var run = PropertyCells.Verify(new Shipped(Order, "UPS", "1Z999", 2.5m),
            Specify<Shipped>().With(x => x.Carrier, "UPS").With(x => x.Weight, 3m));

        run.Columns.ShouldBe(["Carrier", "Weight"]);
        run.Succeeded.ShouldBeFalse();
        run.Cells.Single(c => c.Name == "Weight").Status.ShouldBe(ResultStatus.failed);
        run.Cells.Single(c => c.Name == "Carrier").Status.ShouldBe(ResultStatus.success);
    }

    // --- groups ---

    private static readonly object[] Stream =
    [
        new Shipped(Order, "UPS", "1Z1", 1m),
        new Shipped(Order, "FedEx", "FX2", 2m),
        new Delivered(Order, "Ann")
    ];

    [Fact]
    public void ordered_partials_in_the_right_order_pass()
    {
        ObjectSetVerification.Verify(Stream,
        [
            Specify<Shipped>().With(x => x.Carrier, "UPS"),
            Specify<Shipped>().With(x => x.Carrier, "FedEx"),
            Specify<Delivered>()
        ], "event").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ordered_partials_out_of_order_fail_as_order()
    {
        var run = ObjectSetVerification.Verify(Stream,
        [
            Specify<Shipped>().With(x => x.Carrier, "FedEx"),
            Specify<Shipped>().With(x => x.Carrier, "UPS"),
            Specify<Delivered>()
        ], "event");

        run.Succeeded.ShouldBeFalse();
        ObjectSetVerification.Problems(run, "event").ShouldHaveSingleItem().ShouldStartWith("ORDER");
    }

    [Fact]
    public void any_order_ignores_position()
    {
        ObjectSetVerification.Verify(Stream,
        [
            new Delivered(Order, "Ann"),
            Specify<Shipped>().With(x => x.Carrier, "FedEx"),
            Specify<Shipped>().With(x => x.Carrier, "UPS")
        ], "event", SetMode.AnyOrder).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void any_order_still_flags_an_extra_event()
    {
        var run = ObjectSetVerification.Verify(Stream,
        [
            Specify<Shipped>().With(x => x.Carrier, "FedEx"),
            Specify<Shipped>().With(x => x.Carrier, "UPS")
        ], "event", SetMode.AnyOrder);

        ObjectSetVerification.Problems(run, "event").ShouldHaveSingleItem().ShouldStartWith("EXTRA Delivered");
    }

    [Fact]
    public void pairing_does_not_depend_on_the_order_expectations_are_written()
    {
        // The broad expectation (any Shipped) written FIRST would greedily take the UPS event, leaving
        // the narrow one (Carrier UPS) with only FedEx: a false FAIL. A maximum matching avoids it.
        var run = ObjectSetVerification.Verify(Stream,
        [
            Specify<Shipped>(),
            Specify<Shipped>().With(x => x.Carrier, "UPS"),
            Specify<Delivered>()
        ], "event", SetMode.AnyOrder);

        run.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void contains_allows_other_events()
    {
        ObjectSetVerification.Verify(Stream, [Specify<Delivered>().With(x => x.SignedBy, "Ann")], "event", SetMode.Contains)
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void contains_reports_a_missing_event_and_shows_no_extras()
    {
        var run = ObjectSetVerification.Verify(Stream, [Specify<Cancelled>()], "event", SetMode.Contains);

        ObjectSetVerification.Problems(run, "event").ShouldBe(["MISSING Cancelled()"]);
        run.Cells.ShouldNotContain(c => c.Name == "extra-row");
    }

    [Fact]
    public void the_right_type_with_a_wrong_specified_value_is_a_fail_naming_only_that_member()
    {
        var run = ObjectSetVerification.Verify(Stream,
            [Specify<Delivered>().With(x => x.SignedBy, "Bob")], "event", SetMode.Contains);

        ObjectSetVerification.Problems(run, "event").ShouldBe(["FAIL Delivered: expected SignedBy: Bob, was SignedBy: Ann"]);
    }

    [Fact]
    public void a_matched_partial_row_shows_only_the_specified_members()
    {
        var run = ObjectSetVerification.Verify(Stream,
            [Specify<Shipped>().With(x => x.TrackingNumber, "FX2")], "event", SetMode.Contains);

        run.Cells.Single(c => c.Name == ObjectSetVerification.ValuesColumn).Actual.ShouldBe("TrackingNumber: FX2");
    }

    [Fact]
    public void absent_passes_when_no_event_of_the_type_exists()
    {
        ObjectSetVerification.Absent(Stream, [typeof(Cancelled)], "event").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void absent_fails_when_one_is_present()
    {
        var run = ObjectSetVerification.Absent(Stream, [typeof(Delivered)], "event");

        run.Succeeded.ShouldBeFalse();
        ObjectSetVerification.Problems(run, "event").ShouldHaveSingleItem().ShouldStartWith("PRESENT Delivered");
    }

    [Fact]
    public void absent_with_a_partial_object_forbids_only_matching_events()
    {
        ObjectSetVerification.Absent(Stream, [Specify<Shipped>().With(x => x.Carrier, "DHL")], "event")
            .Succeeded.ShouldBeTrue();
        ObjectSetVerification.Absent(Stream, [Specify<Shipped>().With(x => x.Carrier, "UPS")], "event")
            .Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void the_existing_ordered_boolean_overload_still_works()
    {
        var expected = Stream.ToList();
        ObjectSetVerification.Cells(Stream, expected, (a, i) => PartialMatching.Differences(a, expected[i]), "event", ordered: false)
            .Succeeded.ShouldBeTrue();
    }
}
