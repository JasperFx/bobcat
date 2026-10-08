using Bobcat;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Partial;

/// <summary>bobcat#421: "this is the value of X.Prop when a spec doesn't say".</summary>
public class DeclaredDefaultsTests
{
    public enum Currency { Usd, Eur }

    public record Address(string Street, string Country);

    public record Order(Guid OrderId, string Customer, Currency Currency, decimal Total, Address ShippingAddress, List<string> Tags);

    public class Invoice
    {
        public required string Number { get; init; }
        public required Address BillTo { get; init; }
    }

    [Fact]
    public void a_declared_default_fills_the_member_when_the_spec_does_not_say()
    {
        var defaults = new DeclaredDefaults().For<Order, Currency>(x => x.Currency, Currency.Eur);

        Specify<Order>().Build(defaults).Currency.ShouldBe(Currency.Eur);
    }

    [Fact]
    public void what_the_spec_says_wins_over_the_declared_default()
    {
        var defaults = new DeclaredDefaults().For<Order, Currency>(x => x.Currency, Currency.Eur);

        Specify<Order>().With(x => x.Currency, Currency.Usd).Build(defaults).Currency.ShouldBe(Currency.Usd);
    }

    [Fact]
    public void a_member_with_no_declared_default_falls_back_to_predictable_values()
    {
        var order = Specify<Order>().Build(new DeclaredDefaults().For<Order, string>(x => x.Customer, "Ann"));

        order.Customer.ShouldBe("Ann");
        order.OrderId.ShouldNotBe(Guid.Empty);
        order.Tags.ShouldBeEmpty();
    }

    [Fact]
    public void a_default_on_a_type_applies_wherever_that_type_is_built()
    {
        var defaults = new DeclaredDefaults().For<Address, string>(x => x.Country, "US");

        Specify<Order>().Build(defaults).ShippingAddress.Country.ShouldBe("US");
        Specify<Invoice>().With(x => x.Number, "INV-1").Build(defaults).BillTo.Country.ShouldBe("US");
    }

    [Fact]
    public void a_path_declares_the_default_on_the_last_members_type()
    {
        var defaults = new DeclaredDefaults().For<Order, string>(x => x.ShippingAddress.Country, "CA");

        Specify<Order>().Build(defaults).ShippingAddress.Country.ShouldBe("CA");
        Specify<Invoice>().Build(defaults).BillTo.Country.ShouldBe("CA");
        defaults.Declared.ShouldBe(["Address.Country"]);
    }

    [Fact]
    public void a_factory_gives_each_build_its_own_value()
    {
        var defaults = new DeclaredDefaults().For<Order, List<string>>(x => x.Tags, () => ["new"]);

        var first = Specify<Order>().Build(defaults);
        var second = Specify<Order>().Build(defaults);

        first.Tags.ShouldBe(["new"]);
        first.Tags.ShouldNotBeSameAs(second.Tags);
    }

    [Fact]
    public void a_required_member_takes_its_declared_default()
    {
        var defaults = new DeclaredDefaults().For<Invoice, string>(x => x.Number, "INV-0");

        Specify<Invoice>().Build(defaults).Number.ShouldBe("INV-0");
    }

    [Fact]
    public void the_fallback_can_be_another_policy()
    {
        var defaults = new DeclaredDefaults(DefaultValues.Instance).For<Order, string>(x => x.Customer, "Ann");

        var order = Specify<Order>().Build(defaults);
        order.Customer.ShouldBe("Ann");
        order.OrderId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void a_declaration_that_is_not_a_member_is_refused_by_name()
    {
        Should.Throw<ArgumentException>(() => new DeclaredDefaults().For<Order, string>(x => x.Customer.Trim(), "x"))
            .Message.ShouldContain("x => x.Name");
    }
}

/// <summary>The process-wide policy, which every build without its own uses. Not parallel: it is global.</summary>
[Collection(Name)]
public class ProcessWideUnspecifiedValuesTests
{
    public const string Name = "process-wide-unspecified-values";

    [Fact]
    public void a_build_given_no_policy_uses_the_process_wide_one()
    {
        var original = PartialObjects.UnspecifiedValues;
        PartialObjects.UnspecifiedValues = new DeclaredDefaults()
            .For<DeclaredDefaultsTests.Order, string>(x => x.Customer, "Everyone");
        try
        {
            Specify<DeclaredDefaultsTests.Order>().Build().Customer.ShouldBe("Everyone");

            // The Gherkin lane builds through the same policy
            var row = (DeclaredDefaultsTests.Order)Bobcat.CritterStack.RecordBuilding.Build(
                typeof(DeclaredDefaultsTests.Order), new Dictionary<string, string> { ["Total"] = "3.50" });
            row.Customer.ShouldBe("Everyone");
            row.Total.ShouldBe(3.50m);
        }
        finally
        {
            PartialObjects.UnspecifiedValues = original;
        }
    }

    [Fact]
    public void the_process_wide_policy_starts_as_predictable_values()
    {
        PartialObjects.UnspecifiedValues.ShouldBeSameAs(PredictableValues.Instance);
    }
}

[CollectionDefinition(ProcessWideUnspecifiedValuesTests.Name, DisableParallelization = true)]
public class ProcessWideUnspecifiedValuesCollection;
