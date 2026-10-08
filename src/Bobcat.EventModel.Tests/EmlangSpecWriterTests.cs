using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>bobcat#423: an emlang model's examples as WolverineFx.Bobcat specifications.</summary>
public class EmlangSpecWriterTests
{
    private const string Kitchen =
        """
        slices:
          Add item:
            steps:
              - t: Website / Menu
              - c: Add item to order
              - e: Order / Item added
              - v: Order summary
            tests:
              Add to an existing order:
                given:
                  - e: Order / Order started
                    props:
                      order id: order-123
                when:
                  - c: Add item to order
                    props:
                      order id: order-123
                      item: margherita
                then:
                  - e: Order / Item added
                    props:
                      order id: order-123
                      item: margherita
                  - v: Order summary
                    props:
                      order id: order-123
                      items:
                        - margherita/9.99
                      total: 9.99
              Add to a closed order:
                given:
                  - e: Order / Order closed
                    props:
                      order id: order-9
                when:
                  - c: Add item to order
                    props:
                      order id: order-9
                then:
                  - x: Order is closed
          View summary:
            steps:
              - e: Order / Item added
              - v: Order summary
            tests:
              Nothing ordered yet:
                given:
                then:
        """;

    private static (GeneratedSpecs Specs, CSharpModelWriter.Output Stubs) generate(string yaml)
    {
        var board = EmlangReader.Read(yaml);
        var model = EmlangImport.ToCurated(board, "Kitchen").Model;
        var specs = EmlangSpecWriter.Write(board, model, "Kitchen");
        return (specs, CSharpModelWriter.Write(model, "Kitchen", specs.Additions));
    }

    private static string code(string yaml) => generate(yaml).Specs.Code;

    [Fact]
    public void each_slice_is_a_feature_class_and_each_test_a_fact()
    {
        var (specs, _) = generate(Kitchen);

        specs.Features.ShouldBe(2);
        specs.Specs.ShouldBe(3);
        specs.Code.ShouldContain("[BobcatFeature(\"Add item\")]");
        specs.Code.ShouldContain("public class add_item(AppFixture app) : KitchenSpec(app)");
        specs.Code.ShouldContain("public async Task add_to_an_existing_order()");
    }

    [Fact]
    public void an_identity_is_one_declared_guid_local_named_for_what_it_identifies()
    {
        var code = EmlangSpecWriterTests.code(Kitchen);

        code.ShouldContain("var theOrder = Guid.NewGuid(); // \"order-123\" in the model");
        code.ShouldContain("await GivenEvents<Order>(theOrder, Specify<OrderStarted>().With(x => x.OrderId, theOrder));");
    }

    [Fact]
    public void the_act_and_the_expectations_are_partial_objects_of_exactly_what_the_example_names()
    {
        var code = EmlangSpecWriterTests.code(Kitchen);

        code.ShouldContain("await WhenReceived(Specify<AddItemToOrder>().With(x => x.OrderId, theOrder).With(x => x.Item, \"margherita\"));");
        code.ShouldContain("ThenEvents(Specify<ItemAdded>().With(x => x.OrderId, theOrder).With(x => x.Item, \"margherita\"));");
    }

    [Fact]
    public void a_view_is_checked_on_the_document_its_identity_names()
    {
        EmlangSpecWriterTests.code(Kitchen).ShouldContain(
            "await ThenReadModel<OrderSummary>(theOrder, Specify<OrderSummary>().With(x => x.OrderId, theOrder)"
            + ".With(x => x.Items, new List<string> { \"margherita/9.99\" }).With(x => x.Total, 9.99m));");
    }

    [Fact]
    public void a_refusal_appends_nothing()
    {
        var code = EmlangSpecWriterTests.code(Kitchen);

        code.ShouldContain("ThenRefusedWith(\"Order is closed\");\n        ThenNoEvents();");
    }

    [Fact]
    public void a_view_test_that_expects_nothing_and_names_no_identity_is_no_read_model_at_all()
    {
        generate(Kitchen).Specs.Code.ShouldContain("await ThenNoReadModel<OrderSummary>();");
    }

    [Fact]
    public void the_stubs_carry_what_the_specs_need_streams_document_ids_and_events_only_a_given_names()
    {
        var stubs = generate(Kitchen).Stubs.Stubs;

        stubs.ShouldContain("public class Order { public Guid Id { get; set; } }");
        stubs.ShouldContain("public record OrderSummary(Guid Id, Guid OrderId, List<string> Items, decimal Total);");

        // Named only by a test's given, never by a slice's steps
        stubs.ShouldContain("public record OrderClosed(Guid OrderId);");
    }

    [Fact]
    public void an_empty_given_with_an_act_names_the_stream_the_act_starts()
    {
        code(
            """
            slices:
              Start order:
                tests:
                  First item:
                    when:
                      - c: Start order
                        props:
                          order id: o-1
                    then:
                      - e: Order / Order started
                        props:
                          order id: o-1
            """).ShouldContain("await GivenNoEventsFor<Order>(theOrder);");
    }

    [Fact]
    public void a_declared_type_shapes_the_literal()
    {
        var code = EmlangSpecWriterTests.code(
            """
            slices:
              Book:
                steps:
                  - c: Book session
                    props:
                      starts: datetime
                      seats: int
                      paid: boolean
                tests:
                  Booked:
                    when:
                      - c: Book session
                        props:
                          starts: "2026-08-10T09:00:00Z"
                          seats: 2
                          paid: true
            """);

        code.ShouldContain(".With(x => x.Starts, DateTimeOffset.Parse(\"2026-08-10T09:00:00Z\"))");
        code.ShouldContain(".With(x => x.Seats, 2)");
        code.ShouldContain(".With(x => x.Paid, true)");
    }

    [Fact]
    public void a_value_that_does_not_fit_its_declared_type_still_compiles_and_says_so()
    {
        EmlangSpecWriter.Literal("int", "lots").ShouldBe("default /* lots in the model */");
        EmlangSpecWriter.Literal("string", "say \"hi\"").ShouldBe("\"say \\\"hi\\\"\"");
    }

    [Fact]
    public void names_are_snake_case_and_a_single_word_feature_avoids_a_reserved_lowercase_name()
    {
        EmlangSpecWriter.FeatureClassName("Add item").ShouldBe("add_item");
        EmlangSpecWriter.FeatureClassName("Checkout").ShouldBe("checkout_slice");
        EmlangSpecWriter.MethodName("RejectsOverlappingWindow").ShouldBe("rejects_overlapping_window");
    }

    [Fact]
    public void a_second_run_reports_the_examples_with_no_specification()
    {
        var board = EmlangReader.Read(Kitchen);
        var written =
            """
            [BobcatFeature("Add item")]
            public class add_item(AppFixture app) : KitchenSpec(app)
            {
                [Fact]
                public async Task add_to_an_existing_order() { }
            }
            """;

        EmlangSpecWriter.MissingSpecs(board, [written])
            .ShouldBe(["Add item / Add to a closed order", "View summary / Nothing ordered yet"]);
    }

    [Fact]
    public void no_generated_line_ends_in_whitespace()
    {
        code(Kitchen).Split('\n').Where(x => x.Length > 0 && char.IsWhiteSpace(x[^1])).ShouldBeEmpty();
    }

    [Fact]
    public void a_view_given_is_stored_directly_keyed_by_the_identity_the_test_names()
    {
        var code = EmlangSpecWriterTests.code(
            """
            slices:
              Assign driver:
                tests:
                  Driver assigned:
                    given:
                      - v: Available drivers
                        props:
                          drivers:
                            - driver-456
                    when:
                      - c: Assign driver
                        props:
                          order id: order-123
                    then:
                      - e: Delivery / Driver assigned
                        props:
                          order id: order-123
            """);

        code.ShouldContain("await GivenReadModel<AvailableDrivers>(Specify<AvailableDrivers>().With(x => x.Id, theOrder)"
                           + ".With(x => x.Drivers, new List<string> { \"driver-456\" }));");
        code.ShouldNotContain("TODO: the model arranges");
    }

    [Fact]
    public void a_view_with_no_identity_anywhere_is_checked_as_the_only_one_of_its_type()
    {
        var (specs, _) = generate(
            """
            slices:
              Track available drivers:
                steps:
                  - v: Available drivers
                tests:
                  All available:
                    then:
                      - v: Available drivers
                        props:
                          drivers:
                            - driver-456
            """);

        specs.Code.ShouldContain("await ThenSingleReadModel<AvailableDrivers>(Specify<AvailableDrivers>()"
                                 + ".With(x => x.Drivers, new List<string> { \"driver-456\" }));");
        specs.Report.ShouldContain(x => x.Contains("checked as the only AvailableDrivers"));
    }

    [Fact]
    public void a_refusal_names_the_values_the_model_gives_it()
    {
        code(
            """
            slices:
              Register:
                tests:
                  Email taken:
                    when:
                      - c: Register
                        props:
                          email: joe@example.com
                    then:
                      - x: Email already in use
                        props:
                          email: joe@example.com
            """).ShouldContain("ThenRefusedWith(\"Email already in use\", \"joe@example.com\");");
    }

    [Fact]
    public void a_refusal_naming_an_identity_names_its_local()
    {
        code(
            """
            slices:
              Close:
                tests:
                  Already closed:
                    when:
                      - c: Close order
                        props:
                          order id: order-9
                    then:
                      - x: Order already closed
                        props:
                          order id: order-9
            """).ShouldContain("ThenRefusedWith(\"Order already closed\", theOrder);");
    }

    [Fact]
    public void an_automation_slice_says_what_triggers_it()
    {
        code(
            """
            slices:
              Send receipt:
                steps:
                  - e: Order / Payment confirmed
                  - c: Send receipt
                  - e: Receipt sent
                tests:
                  Sent:
                    when:
                      - c: Send receipt
                    then:
                      - e: Receipt sent
            """).ShouldContain("// SendReceipt is an automation, triggered by \"Payment confirmed\"\n[BobcatFeature(\"Send receipt\")]");
    }

    [Fact]
    public void a_field_only_an_unattached_example_names_is_still_on_the_stub()
    {
        // A slice with no steps segments into nothing, so its tests attach to no slice and their
        // props never become hints: the specs name the fields anyway, so the stubs must have them
        var stubs = generate(
            """
            slices:
              Assign driver:
                tests:
                  Driver assigned:
                    when:
                      - c: Assign driver
                        props:
                          order id: order-123
                          vehicle: van
                          seats: 2
            """).Stubs.Stubs;

        stubs.ShouldContain("public record AssignDriver(Guid OrderId, string Vehicle, int Seats);");
    }
}
