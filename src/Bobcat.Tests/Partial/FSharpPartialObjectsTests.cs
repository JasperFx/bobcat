using Bobcat;
using Bobcat.Tests.FSharpTypes;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Partial;

public class FSharpPartialObjectsTests
{
    [Fact]
    public void an_fsharp_record_takes_the_specified_members_and_fills_the_rest()
    {
        var shipment = Specify<ShipmentConfirmed>().With(x => x.TrackingNumber, "1Z999").Build();

        shipment.TrackingNumber.ShouldBe("1Z999");
        shipment.OrderId.Item.ShouldNotBe(Guid.Empty);
        shipment.Carrier.ShouldBeNull(); // None
        shipment.Weight.ShouldBe(0m);
        shipment.Status.ShouldBe(Status.Pending);
        shipment.Notes.IsEmpty.ShouldBeTrue();
        shipment.Tags.IsEmpty.ShouldBeTrue();
        shipment.ShippedTo.City.ShouldBe("");
    }

    [Fact]
    public void an_option_from_a_typed_value()
    {
        Specify<ShipmentConfirmed>().With(x => x.Carrier, FSharpOption<string>.Some("UPS")).Build()
            .Carrier.Value.ShouldBe("UPS");
    }

    [Fact]
    public void an_option_from_its_inner_value()
    {
        Specify<ShipmentConfirmed>().With(nameof(ShipmentConfirmed.Carrier), "UPS").Build()
            .Carrier.Value.ShouldBe("UPS");
    }

    [Fact]
    public void an_option_from_cells()
    {
        StepTable table = """
                          | Carrier | Weight |
                          | UPS     | 2.5    |
                          """;

        var shipment = (ShipmentConfirmed)PartialObjects.FromTable(typeof(ShipmentConfirmed), table).Single().Build();

        shipment.Carrier.Value.ShouldBe("UPS");
        shipment.Weight.ShouldBe(2.5m);
    }

    [Fact]
    public void null_in_a_cell_is_none()
    {
        StepTable table = """
                          | Carrier |
                          | NULL    |
                          """;

        ((ShipmentConfirmed)PartialObjects.FromTable(typeof(ShipmentConfirmed), table).Single().Build())
            .Carrier.ShouldBeNull();
    }

    [Fact]
    public void a_single_case_union_from_its_wrapped_value()
    {
        var id = Guid.NewGuid();

        Specify<ShipmentConfirmed>().With(nameof(ShipmentConfirmed.OrderId), id).Build().OrderId.Item.ShouldBe(id);
    }

    [Fact]
    public void a_single_case_union_from_a_cell()
    {
        var id = Guid.NewGuid();
        StepTable table = $"""
                           | OrderId |
                           | {id}    |
                           """;

        ((ShipmentConfirmed)PartialObjects.FromTable(typeof(ShipmentConfirmed), table).Single().Build())
            .OrderId.Item.ShouldBe(id);
    }

    [Fact]
    public void a_single_case_union_as_itself()
    {
        var id = OrderId.NewOrderId(Guid.NewGuid());

        Specify<ShipmentConfirmed>().With(x => x.OrderId, id).Build().OrderId.ShouldBe(id);
    }

    [Fact]
    public void an_fsharp_list_and_set_from_cells()
    {
        StepTable table = """
                          | Notes        | Tags   |
                          | fragile, top | b, a   |
                          """;

        var shipment = (ShipmentConfirmed)PartialObjects.FromTable(typeof(ShipmentConfirmed), table).Single().Build();

        shipment.Notes.ToArray().ShouldBe(["fragile", "top"]);
        shipment.Tags.ToArray().ShouldBe(["a", "b"]);
    }

    [Fact]
    public void an_fsharp_list_from_a_typed_list()
    {
        Specify<ShipmentConfirmed>().With(x => x.Notes, ListModule.OfArray(new[] { "x" })).Build()
            .Notes.Single().ShouldBe("x");
    }

    [Fact]
    public void an_fsharp_list_from_a_csharp_collection()
    {
        Specify<ShipmentConfirmed>().With(nameof(ShipmentConfirmed.Notes), new[] { "x", "y" }).Build()
            .Notes.ToArray().ShouldBe(["x", "y"]);
    }

    [Fact]
    public void a_nested_fsharp_record_member()
    {
        Specify<ShipmentConfirmed>().With(x => x.ShippedTo.City, "Austin").Build().ShippedTo.City.ShouldBe("Austin");
    }

    [Fact]
    public void a_climutable_record()
    {
        var built = Specify<MutableShipment>().With(x => x.Name, "crate").Build();

        built.Name.ShouldBe("crate");
        built.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void an_fsharp_class_with_a_constructor_and_a_mutable_property()
    {
        var built = Specify<Shipment>().With(x => x.Name, "crate").With(x => x.Count, 3).Build();

        built.Name.ShouldBe("crate");
        built.Count.ShouldBe(3);
    }

    [Fact]
    public void a_value_option()
    {
        Specify<Discount>().Build().Percent.IsNone.ShouldBeTrue();

        StepTable table = """
                          | Percent |
                          | 15      |
                          """;

        ((Discount)PartialObjects.FromTable(typeof(Discount), table).Single().Build()).Percent.Value.ShouldBe(15m);
    }

    [Fact]
    public void an_fsharp_record_member_matched_by_its_constructor_parameter_ignoring_case()
    {
        StepTable table = """
                          | trackingNumber |
                          | 1Z999          |
                          """;

        ((ShipmentConfirmed)PartialObjects.FromTable(typeof(ShipmentConfirmed), table).Single().Build())
            .TrackingNumber.ShouldBe("1Z999");
    }

    [Fact]
    public void an_fsharp_record_member_is_read_only_outside_the_constructor()
    {
        // Every field is a constructor parameter, so nothing is out of reach — the unknown-member
        // rule still names a typo.
        Should.Throw<Bobcat.Engine.SpecCriticalException>(() =>
                Specify<ShipmentConfirmed>().With("TrackingNo", "1Z").Build())
            .Message.ShouldContain("TrackingNumber");
    }

    [Fact]
    public void authored_in_fsharp()
    {
        var shipment = Authoring.trackingOnly();

        shipment.TrackingNumber.ShouldBe("1Z999");
        shipment.ShippedTo.City.ShouldBe("Austin");

        Authoring.mutableShipment().Count.ShouldBe(3);
    }
}
