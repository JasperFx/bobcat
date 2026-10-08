using Bobcat;
using Bobcat.Engine;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Partial;

public class BuildingPartialObjectsTests
{
    [Fact]
    public void a_positional_record_takes_the_specified_members_and_fills_the_rest()
    {
        var clock = new ControllableTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        BobcatClock.Set(clock);
        try
        {
            var order = Specify<PositionalOrder>()
                .With(x => x.Customer, "Ann")
                .With(x => x.Total, 12.50m)
                .Build();

            order.Customer.ShouldBe("Ann");
            order.Total.ShouldBe(12.50m);

            order.OrderId.ShouldNotBe(Guid.Empty);
            order.Lines.ShouldBe(0);
            order.Status.ShouldBe(OrderStatus.Placed);
            order.PlacedAt.ShouldBe(clock.GetUtcNow());
        }
        finally
        {
            BobcatClock.Reset();
        }
    }

    [Fact]
    public void two_unspecified_guids_are_different_identities()
    {
        var first = Specify<PositionalOrder>().Build();
        var second = Specify<PositionalOrder>().Build();

        first.OrderId.ShouldNotBe(second.OrderId);
    }

    [Fact]
    public void an_unspecified_non_nullable_string_is_empty_not_null()
    {
        Specify<PositionalOrder>().Build().Customer.ShouldBe("");
    }

    [Fact]
    public void a_record_with_a_primary_constructor_and_init_properties_gets_both()
    {
        var record = Specify<MixedRecord>()
            .With(x => x.Name, "Widget")
            .With(x => x.Note, "fragile")
            .With(x => x.Priority, 3)
            .Build();

        record.Name.ShouldBe("Widget");
        record.Note.ShouldBe("fragile");
        record.Priority.ShouldBe(3);
        record.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void an_unspecified_init_property_keeps_the_types_own_initializer()
    {
        var record = Specify<MixedRecord>().With(x => x.Name, "Widget").Build();

        record.Region.ShouldBe("unassigned");
        record.Note.ShouldBeNull();
    }

    [Fact]
    public void a_record_struct()
    {
        var point = Specify<Point>().With(x => x.Y, 7).Build();

        point.X.ShouldBe(0);
        point.Y.ShouldBe(7);
    }

    [Fact]
    public void a_readonly_record_struct()
    {
        var money = Specify<Money>().With(x => x.Amount, 5m).Build();

        money.Amount.ShouldBe(5m);
        money.Currency.ShouldBe("");
    }

    [Fact]
    public void a_class_with_settable_properties()
    {
        var customer = Specify<SettableCustomer>()
            .With(x => x.Name, "Ann")
            .With(x => x.Visits, 4)
            .Build();

        customer.Name.ShouldBe("Ann");
        customer.Visits.ShouldBe(4);
    }

    [Fact]
    public void unspecified_settable_members_keep_initializers_and_nullable_members_stay_null()
    {
        var customer = Specify<SettableCustomer>().With(x => x.Name, "Ann").Build();

        customer.Tags.ShouldBe(["seeded"]);
        customer.Nickname.ShouldBeNull();
        customer.Address.ShouldBeNull();
        customer.LastSeen.ShouldBeNull();
    }

    [Fact]
    public void a_non_nullable_member_the_type_left_null_is_filled()
    {
        var customer = Specify<UninitializedCustomer>().Build();

        customer.Name.ShouldBe("");
        customer.Scores.ShouldBeEmpty();
        customer.Address.ShouldNotBeNull();
        customer.Address.City.ShouldBe("");
    }

    [Fact]
    public void a_class_with_init_properties()
    {
        var customer = Specify<InitCustomer>().With(x => x.Visits, 2).Build();

        customer.Visits.ShouldBe(2);
        customer.Name.ShouldBe("initial");
    }

    [Fact]
    public void unspecified_required_members_are_filled()
    {
        var customer = Specify<RequiredCustomer>().With(x => x.Age, 40).Build();

        customer.Age.ShouldBe(40);
        customer.Name.ShouldBe("");
        customer.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void specified_required_members_are_set()
    {
        var id = Guid.NewGuid();
        var customer = Specify<RequiredCustomer>().With(x => x.Id, id).With(x => x.Name, "Ann").Build();

        customer.Id.ShouldBe(id);
        customer.Name.ShouldBe("Ann");
    }

    [Fact]
    public void a_constructor_with_private_setters_and_a_public_setter()
    {
        var built = Specify<PrivateSetters>()
            .With(x => x.Name, "Ann")
            .With(x => x.Count, 5)
            .Build();

        built.Name.ShouldBe("Ann");
        built.Count.ShouldBe(5);
        built.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void an_immutable_class_uses_its_own_default_for_an_unspecified_optional_parameter()
    {
        var line = Specify<ImmutableLine>().With(x => x.Sku, "ABC").Build();

        line.Sku.ShouldBe("ABC");
        line.Quantity.ShouldBe(0);
        line.Price.ShouldBe(9.99m);
    }

    [Fact]
    public void a_read_only_member_no_constructor_takes_is_refused_by_name()
    {
        var thrown = Should.Throw<SpecCriticalException>(() =>
            Specify<ImmutableLine>().With(x => x.Total, 100m).Build());

        thrown.Message.ShouldContain("'ImmutableLine.Total' is read-only");
    }

    [Fact]
    public void the_constructor_binding_the_most_specified_members_wins()
    {
        var built = Specify<SeveralConstructors>().With(x => x.Name, "Ann").With(x => x.Count, 2).Build();

        built.Name.ShouldBe("Ann");
        built.Count.ShouldBe(2);
    }

    [Fact]
    public void with_nothing_a_constructor_takes_the_parameterless_constructor_is_used()
    {
        var built = Specify<SeveralConstructors>().With(x => x.Label, "x").Build();

        built.Name.ShouldBe("none");
        built.Label.ShouldBe("x");
    }

    [Fact]
    public void public_fields()
    {
        var built = Specify<WithFields>().With(x => x.Name, "Ann").With(x => x.Count, 3).Build();

        built.Name.ShouldBe("Ann");
        built.Count.ShouldBe(3);
    }

    [Fact]
    public void a_readonly_field_cannot_be_specified()
    {
        Should.Throw<SpecCriticalException>(() => Specify<WithFields>().With(x => x.Fixed, "other").Build())
            .Message.ShouldContain("'WithFields.Fixed' is read-only");
    }

    [Fact]
    public void a_nested_member_by_path()
    {
        var customer = Specify<Customer>().With(x => x.Address.City, "Austin").Build();

        customer.Address.City.ShouldBe("Austin");
        customer.Address.Street.ShouldBe("");
        customer.Name.ShouldBe("");
    }

    [Fact]
    public void a_nested_partial_object()
    {
        var customer = Specify<Customer>()
            .With(x => x.Name, "Ann")
            .With(x => x.Address, Specify<CustomerAddress>().With(a => a.City, "Austin").With(a => a.Zip, "78701"))
            .Build();

        customer.Address.City.ShouldBe("Austin");
        customer.Address.Zip.ShouldBe("78701");
        customer.Name.ShouldBe("Ann");
        customer.Address.Street.ShouldBe("");
    }

    [Fact]
    public void a_whole_nested_object()
    {
        var address = new CustomerAddress("1 Main", "Austin", "78701");
        Specify<Customer>().With(x => x.Address, address).Build().Address.ShouldBeSameAs(address);
    }

    [Fact]
    public void a_member_specified_both_whole_and_by_its_members_is_refused()
    {
        Should.Throw<SpecCriticalException>(() => Specify<Customer>()
                .With(x => x.Address, new CustomerAddress("1 Main", "Austin", "78701"))
                .With(x => x.Address.City, "Dallas")
                .Build())
            .Message.ShouldContain("'Address' is specified both as a whole and by its members");
    }

    [Fact]
    public void an_unspecified_self_referencing_graph_stops_rather_than_recursing_forever()
    {
        var built = Specify<SelfReferencing>().With(x => x.Name, "root").Build();

        built.Child.ShouldNotBeNull();
        built.Child.Owner.ShouldBeNull();
    }

    [Fact]
    public void a_specified_member_of_the_same_type_is_built()
    {
        var node = Specify<Node>().With(x => x.Name, "leaf").With(x => x.Parent!.Name, "root").Build();

        node.Parent!.Name.ShouldBe("root");
        node.Parent.Parent.ShouldBeNull();
    }

    [Fact]
    public void a_nullable_self_reference_stays_null()
    {
        Specify<Node>().With(x => x.Name, "leaf").Build().Parent.ShouldBeNull();
    }

    [Fact]
    public void specifying_a_member_twice_keeps_the_later_value()
    {
        var partial = Specify<PositionalOrder>().With(x => x.Customer, "Ann").With(x => x.Customer, "Bob");

        partial.Values.Count.ShouldBe(1);
        partial.Build().Customer.ShouldBe("Bob");
    }

    [Fact]
    public void an_expression_that_is_not_a_member_path_is_refused()
    {
        Should.Throw<ArgumentException>(() => Specify<PositionalOrder>().With(x => x.Customer.ToUpper(), "ANN"));
    }

    [Fact]
    public void a_nameof_path_reads_a_string_value_as_a_cell()
    {
        var order = Specify<PositionalOrder>()
            .With(nameof(PositionalOrder.Total), "12.50")
            .With(nameof(PositionalOrder.Status), "shipped")
            .Build();

        order.Total.ShouldBe(12.50m);
        order.Status.ShouldBe(OrderStatus.Shipped);
    }

    [Fact]
    public void a_nameof_path_converts_a_compatible_typed_value()
    {
        Specify<PositionalOrder>().With(nameof(PositionalOrder.Total), 5).Build().Total.ShouldBe(5m);
    }

    [Fact]
    public void an_unknown_member_is_refused_naming_what_the_type_has()
    {
        var thrown = Should.Throw<SpecCriticalException>(() =>
            Specify<PositionalOrder>().With("Custmer", "Ann").Build());

        thrown.Message.ShouldContain("[Custmer] matches nothing on 'PositionalOrder'");
        thrown.Message.ShouldContain("Customer");
    }

    [Fact]
    public void null_for_a_non_nullable_value_type_is_refused()
    {
        Should.Throw<BadCellException>(() => Specify<PositionalOrder>().With(nameof(PositionalOrder.Lines), null).Build())
            .Message.ShouldContain("'Lines' is a Int32, which cannot be null");
    }

    [Fact]
    public void nullable_members()
    {
        var parent = Guid.NewGuid();
        var built = Specify<Nullables>().With(x => x.Count, 3).With(x => x.ParentId, parent).Build();

        built.Count.ShouldBe(3);
        built.ParentId.ShouldBe(parent);
        built.Note.ShouldBeNull();
        built.When.ShouldBeNull();
    }

    [Fact]
    public void collections_from_typed_values()
    {
        var basket = Specify<Basket>()
            .With(x => x.Skus, ["A", "B"])
            .With(x => x.Quantities, [1, 2])
            .Build();

        basket.Skus.ShouldBe(["A", "B"]);
        basket.Quantities.ShouldBe([1, 2]);
        basket.Codes.ShouldBeEmpty();
        basket.Counts.ShouldBeEmpty();
    }

    [Fact]
    public void a_header_title_names_the_member()
    {
        Specify<Titled>().With("Order #", "A-1").Build().OrderNumber.ShouldBe("A-1");
    }

    [Fact]
    public void a_collection_index_is_refused()
    {
        Should.Throw<SpecCriticalException>(() => Specify<Basket>().With("Skus[0]", "A").Build())
            .Message.ShouldContain("indexes into a collection");
    }

    [Fact]
    public void a_custom_policy_fills_unspecified_members()
    {
        var order = Specify<PositionalOrder>().With(x => x.Total, 1m).Build(new FixedStrings("n/a"));

        order.Customer.ShouldBe("n/a");
        order.Total.ShouldBe(1m);
    }

    [Fact]
    public void the_description_names_only_the_specified_members()
    {
        Specify<PositionalOrder>().With(x => x.Customer, "Ann").With(x => x.Total, 12.5m).ToString()
            .ShouldBe("PositionalOrder(Customer: Ann, Total: 12.5)");
    }

    [Fact]
    public void a_step_name_leads_a_construction_failure()
    {
        var partial = PartialObjects.FromCells(typeof(PositionalOrder), new Dictionary<string, string> { ["Nope"] = "1" });

        Should.Throw<SpecCriticalException>(() => PartialObjects.Build(partial, step: "Given an order"))
            .Message.ShouldStartWith("'Given an order': ");
    }

    private sealed class FixedStrings(string value) : IUnspecifiedValues
    {
        public bool TryValueFor(UnspecifiedMember member, out object? result)
        {
            if (member.Type == typeof(string))
            {
                result = value;
                return true;
            }

            return PredictableValues.Instance.TryValueFor(member, out result);
        }
    }
}
