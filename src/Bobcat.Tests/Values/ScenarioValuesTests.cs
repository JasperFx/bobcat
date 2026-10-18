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
            .ShouldBe("OrderStarted(OrderId: #Order, CustomerId: #Customer, Total: 100)");
    }

    [Fact]
    public void a_value_keeps_the_name_it_was_first_given_wherever_it_appears_again()
    {
        using var _ = scenario();
        var order = Guid.NewGuid();

        ScenarioValues.Describe(new OrderStarted(order, Guid.NewGuid(), 1m));

        ScenarioValues.Describe(new OrderShipped(order, "UPS"))
            .ShouldBe("OrderShipped(OrderId: #Order, Carrier: \"UPS\")");
        ScenarioValues.Format(order).ShouldBe("#Order");
    }

    [Fact]
    public void a_second_value_under_the_same_property_is_numbered()
    {
        using var _ = scenario();

        ScenarioValues.Describe(new OrderShipped(Guid.NewGuid(), "UPS"));
        ScenarioValues.Describe(new OrderShipped(Guid.NewGuid(), "FedEx"))
            .ShouldBe("OrderShipped(OrderId: #Order2, Carrier: \"FedEx\")");
    }

    [Fact]
    public void a_bare_id_is_named_after_its_type_and_collections_read_in_brackets()
    {
        using var _ = scenario();

        ScenarioValues.Describe(new Order(Guid.NewGuid(), ["a", "b"]))
            .ShouldBe("Order(Id: #Order, Lines: [\"a\", \"b\"])");
    }

    [Fact]
    public void a_name_given_by_hand_wins()
    {
        using var _ = scenario();
        var customer = Guid.NewGuid();
        ScenarioValues.Name(customer, "Alice");

        ScenarioValues.Describe(new OrderStarted(Guid.NewGuid(), customer, 1m)).ShouldContain("CustomerId: #Alice");
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

public class DescribePropertiesTests
{
    [Fact]
    public void the_properties_without_the_type_around_them()
    {
        using var _ = ScenarioRecorder.Begin("Values", "props", publisher: null, runId: Guid.NewGuid());
        ScenarioValues.DescribeProperties(new OrderShipped(Guid.NewGuid(), "UPS")).ShouldBe("OrderId: #Order, Carrier: \"UPS\"");
    }

    [Fact]
    public void a_scalar_reads_as_itself_and_a_stub_has_nothing_to_say()
    {
        // bobcat#618: "│ BookReturned │ BookReturned │" repeated the type column in the values column
        ScenarioValues.DescribeProperties(new Stub()).ShouldBe("");
        ScenarioValues.DescribeProperties(42).ShouldBe("42");
    }

    [Fact]
    public void an_anonymous_object_shows_its_members()
    {
        ScenarioValues.DescribeProperties(new { Name = "Ann" }).ShouldBe("Name: \"Ann\"");
    }
}

// bobcat#618: step text names types and values the way a reader writes them
public class ReadableTypeNameTests
{
    [Fact]
    public void a_generic_type_reads_as_csharp_writes_it()
    {
        ScenarioValues.TypeName(typeof(List<Order>)).ShouldBe("List<Order>");
        ScenarioValues.TypeName(typeof(IReadOnlyList<Order>)).ShouldBe("IReadOnlyList<Order>");
        ScenarioValues.TypeName(typeof(Dictionary<string, int?>)).ShouldBe("Dictionary<string, int?>");
        ScenarioValues.TypeName(typeof(string[])).ShouldBe("string[]");
        ScenarioValues.TypeName(typeof(Order)).ShouldBe("Order");
    }

    [Fact]
    public void an_anonymous_type_has_no_compiler_name()
    {
        ScenarioValues.TypeName(new { }.GetType()).ShouldBe("anonymous object");
        ScenarioValues.TypeName(new { A = 1 }.GetType()).ShouldBe("anonymous object");
    }

    [Fact]
    public void an_anonymous_object_reads_as_its_members()
    {
        ScenarioValues.Describe(new { }).ShouldBe("an empty object");
        ScenarioValues.Describe(new { Name = "Ann", Age = 3 }).ShouldBe("{ Name: \"Ann\", Age: 3 }");
    }
}

// bobcat#416: a partial object names its values exactly as the whole object would
public class PartialObjectValueNamesTests
{
    private static ScenarioRecorder.Recording scenario()
        => ScenarioRecorder.Begin("Values", "a scenario", publisher: null, runId: Guid.NewGuid());

    [Fact]
    public void a_partial_object_names_its_specified_ids_as_the_whole_object_would()
    {
        using var _ = scenario();
        var order = Guid.NewGuid();

        PartialObjects.Describe(Specifications.Specify<OrderShipped>().With(x => x.OrderId, order))
            .ShouldBe("OrderShipped(OrderId: #Order)");
        ScenarioValues.Describe(new OrderStarted(order, Guid.NewGuid(), 1m)).ShouldStartWith("OrderStarted(OrderId: #Order,");
    }

    [Fact]
    public void a_bare_id_in_a_partial_is_named_after_its_type()
    {
        using var _ = scenario();

        PartialMatching.DescribeExpected(Specifications.Specify<Order>().With(x => x.Id, Guid.NewGuid()))
            .ShouldBe("Id: #Order");
    }
}

// A value declared under the name of the variable it was assigned to (the generator's interceptor)
public class DeclaredValueNamesTests
{
    private static ScenarioRecorder.Recording scenario()
        => ScenarioRecorder.Begin("Values", "a scenario", publisher: null, runId: Guid.NewGuid());

    [Fact]
    public void a_declared_value_reads_as_its_variable_name()
    {
        using var _ = scenario();
        var theOrder = ScenarioValues.Declare(Guid.NewGuid(), "theOrder");

        ScenarioValues.Describe(new OrderShipped(theOrder, "UPS")).ShouldBe("OrderShipped(OrderId: #theOrder, Carrier: \"UPS\")");
    }

    [Fact]
    public void a_value_declared_before_the_scenario_began_is_named_when_first_met()
    {
        // A field initialised when the test class is constructed, before its scenario begins
        var theOrder = ScenarioValues.Declare(Guid.NewGuid(), "theOrder");

        using var _ = scenario();
        ScenarioValues.Format(theOrder).ShouldBe("#theOrder");
    }

    [Fact]
    public void a_declared_name_beats_one_learned_from_a_property_even_when_the_property_came_first()
    {
        var theOrder = ScenarioValues.Declare(Guid.NewGuid(), "theOrder");

        using var _ = scenario();
        ScenarioValues.Learn(theOrder, "Order");

        ScenarioValues.Describe(new OrderStarted(theOrder, Guid.NewGuid(), 1m))
            .ShouldStartWith("OrderStarted(OrderId: #theOrder, CustomerId: #Customer");
    }

    [Fact]
    public void a_hand_given_name_still_beats_a_declared_one()
    {
        using var _ = scenario();
        var theOrder = ScenarioValues.Declare(Guid.NewGuid(), "theOrder");
        ScenarioValues.Name(theOrder, "the big order");

        ScenarioValues.Format(theOrder).ShouldBe("#the big order");
    }

    [Fact]
    public void outside_a_scenario_a_declared_guid_is_still_shortened()
    {
        var theOrder = ScenarioValues.Declare(Guid.NewGuid(), "theOrder");
        ScenarioValues.Format(theOrder).ShouldBe(theOrder.ToString()[..8] + "…");
    }

    public class Todo
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
    }

    [Fact]
    public void a_document_leaves_out_an_id_the_store_has_yet_to_assign()
    {
        // bobcat#682: a HiLo id is 0 until the document is stored, so "Id: 0" described a value it never has
        using var _ = scenario();
        ScenarioValues.DescribeDocument(new Todo { Title = "Duplicate" }).ShouldBe("Title: \"Duplicate\"");
        ScenarioValues.DescribeDocument(new Todo { Id = 2, Title = "Duplicate" }).ShouldBe("Id: 2, Title: \"Duplicate\"");
        ScenarioValues.DescribeDocument(new Ghost()).ShouldBe("");
        ScenarioValues.DescribeProperties(new Todo { Title = "Duplicate" }).ShouldBe("Id: 0, Title: \"Duplicate\"");
    }

    public class Ghost
    {
        public Guid Id { get; set; }
    }

    [Fact]
    public void a_duration_inside_a_value_reads_as_words()
    {
        ScenarioValues.Describe(new Reminder(TimeSpan.FromDays(5))).ShouldBe("Reminder(Delay: 5 days)");
    }

    public record Reminder(TimeSpan Delay);
}
