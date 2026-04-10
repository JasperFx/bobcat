using Shouldly;

namespace Bobcat.Tests.Runtime;

public class FixtureTitleTests
{
    [Fact]
    public void derives_title_from_class_name()
    {
        Fixture.DeriveTitle(typeof(OrderAggregateFixture)).ShouldBe("Order Aggregate");
    }

    [Fact]
    public void uses_explicit_title_attribute()
    {
        Fixture.DeriveTitle(typeof(CustomTitleFixture)).ShouldBe("My Custom Feature");
    }

    [Fact]
    public void strips_fixture_suffix()
    {
        Fixture.DeriveTitle(typeof(ShippingFixture)).ShouldBe("Shipping");
    }

    [Fact]
    public void handles_single_word()
    {
        Fixture.DeriveTitle(typeof(CalculatorFixture)).ShouldBe("Calculator");
    }

    [Fact]
    public void pascal_case_splitting()
    {
        Fixture.PascalCaseToTitle("OrderAggregate").ShouldBe("Order Aggregate");
        Fixture.PascalCaseToTitle("PlaceOrder").ShouldBe("Place Order");
        Fixture.PascalCaseToTitle("MultiWordFeatureName").ShouldBe("Multi Word Feature Name");
    }
}

// Test fixtures
public class OrderAggregateFixture : Fixture { }
public class ShippingFixture : Fixture { }
public class CalculatorFixture : Fixture { }

[FixtureTitle("My Custom Feature")]
public class CustomTitleFixture : Fixture { }
