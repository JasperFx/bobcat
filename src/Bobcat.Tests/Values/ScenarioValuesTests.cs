using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Values;

public record OrderStarted(Guid OrderId, Guid CustomerId, decimal Total);
public record OrderShipped(Guid OrderId, string Carrier);
public record Order(Guid Id, List<string> Lines);
public record Stub;

public class ScenarioValuesTests
{
    private static ScenarioRecorder.Recording scenario()
        => ScenarioRecorder.Begin("Values", "a scenario", publisher: null, runId: Guid.NewGuid());

    [Fact]
    public void a_record_reads_as_its_type_and_properties_on_one_line()
    {
        using var _ = scenario();
        var order = Guid.NewGuid();
        var customer = Guid.NewGuid();

        ScenarioValues.Describe(new OrderStarted(order, customer, 100m))
            .ShouldBe("OrderStarted(OrderId: Order, CustomerId: Customer, Total: 100)");
    }

    [Fact]
    public void a_value_keeps_the_name_it_was_first_given_wherever_it_appears_again()
    {
        using var _ = scenario();
        var order = Guid.NewGuid();

        ScenarioValues.Describe(new OrderStarted(order, Guid.NewGuid(), 1m));

        ScenarioValues.Describe(new OrderShipped(order, "UPS"))
            .ShouldBe("OrderShipped(OrderId: Order, Carrier: \"UPS\")");
        ScenarioValues.Format(order).ShouldBe("Order");
    }

    [Fact]
    public void a_second_value_under_the_same_property_is_numbered()
    {
        using var _ = scenario();

        ScenarioValues.Describe(new OrderShipped(Guid.NewGuid(), "UPS"));
        ScenarioValues.Describe(new OrderShipped(Guid.NewGuid(), "FedEx"))
            .ShouldBe("OrderShipped(OrderId: Order2, Carrier: \"FedEx\")");
    }

    [Fact]
    public void a_bare_id_is_named_after_its_type_and_collections_read_in_brackets()
    {
        using var _ = scenario();

        ScenarioValues.Describe(new Order(Guid.NewGuid(), ["a", "b"]))
            .ShouldBe("Order(Id: Order, Lines: [\"a\", \"b\"])");
    }

    [Fact]
    public void a_name_given_by_hand_wins()
    {
        using var _ = scenario();
        var customer = Guid.NewGuid();
        ScenarioValues.Name(customer, "Alice");

        ScenarioValues.Describe(new OrderStarted(Guid.NewGuid(), customer, 1m)).ShouldContain("CustomerId: Alice");
    }

    [Fact]
    public void a_stub_is_its_type_name()
    {
        using var _ = scenario();
        ScenarioValues.Describe(new Stub()).ShouldBe("Stub");
    }

    [Fact]
    public void outside_a_scenario_a_guid_is_shortened_rather_than_named()
    {
        var id = Guid.Parse("7ccea29f-be37-42fe-9678-174737f720c7");
        ScenarioValues.Describe(new OrderShipped(id, "UPS")).ShouldBe("OrderShipped(OrderId: 7ccea29f…, Carrier: \"UPS\")");
    }

    [Fact]
    public void the_names_report_says_what_each_name_stands_for()
    {
        using var recording = scenario();
        var order = Guid.NewGuid();
        ScenarioValues.Describe(new OrderShipped(order, "UPS"));

        var report = SpecReport.For<NamedValuesReport>();
        report.NameOf(order).ShouldBe("Order");
        report.Cells.ShouldContain(c => c.Name == "value" && c.DisplayText == order.ToString());
    }
}

public class ObjectSetVerificationTests
{
    private static readonly Guid Order = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();

    private static TableRun verify(object[] actual, object[] expected, bool ordered = true)
        => ObjectSetVerification.Cells(actual, expected,
            (a, i) => a.Equals(expected[i])
                ? []
                : a.GetType().GetProperties()
                    .Where(p => !Equals(p.GetValue(a), p.GetValue(expected[i])))
                    .Select(p => new ValueDifference(p.Name, p.GetValue(expected[i]), p.GetValue(a)))
                    .ToList(),
            "event", ordered);

    private static string[] problems(TableRun run) => ObjectSetVerification.Problems(run, "event").ToArray();

    [Fact]
    public void the_same_events_in_the_same_order_succeed()
    {
        var events = new object[] { new OrderStarted(Order, Customer, 1m), new OrderShipped(Order, "UPS") };
        verify(events, events).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void one_missing_event_is_one_missing_row_not_every_later_row_wrong()
    {
        var started = new OrderStarted(Order, Customer, 1m);
        var shipped = new OrderShipped(Order, "UPS");

        var run = verify([shipped], [started, shipped]);

        run.Succeeded.ShouldBeFalse();
        problems(run).ShouldHaveSingleItem().ShouldStartWith("MISSING OrderStarted(");
    }

    [Fact]
    public void an_event_nobody_expected_is_extra()
    {
        var shipped = new OrderShipped(Order, "UPS");
        var run = verify([new OrderStarted(Order, Customer, 1m), shipped], [shipped]);

        problems(run).ShouldHaveSingleItem().ShouldStartWith("EXTRA OrderStarted(");
    }

    [Fact]
    public void the_right_event_with_a_wrong_value_is_one_failed_row_naming_the_value()
    {
        var run = verify([new OrderStarted(Order, Customer, 90m)], [new OrderStarted(Order, Customer, 100m)]);

        problems(run).ShouldHaveSingleItem().ShouldBe("FAIL OrderStarted: expected Total: 100, was Total: 90");
    }

    [Fact]
    public void the_right_events_in_the_wrong_order_are_reported_as_order_only_when_ordered()
    {
        var started = new OrderStarted(Order, Customer, 1m);
        var shipped = new OrderShipped(Order, "UPS");

        problems(verify([shipped, started], [started, shipped])).ShouldHaveSingleItem().ShouldStartWith("ORDER ");
        verify([shipped, started], [started, shipped], ordered: false).Succeeded.ShouldBeTrue();
    }
}
