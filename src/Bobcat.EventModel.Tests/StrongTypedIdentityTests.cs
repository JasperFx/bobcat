using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#512 and #433: <c>--strong-typed-ids</c> gives each aggregate an identity type, and every
/// member, specification and declaration that addresses that stream takes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The generated code is compiled, not matched.</b> A text assertion passes for a file with a
/// missing brace or a struct no store would recognise as an identity; what matters about an id type
/// is that the output builds and that the definition says what it says, so these go through
/// <see cref="CSharpModelWriterTests.compile"/> and the compiled descriptor, as #405's own tests do.
/// </para>
/// <para>
/// <b>The specifications matter as much as the stubs.</b> Typing a stub's member without teaching the
/// spec writer would produce a <c>Guid</c> local handed to an <c>OrderId</c> member — an import whose
/// two halves do not compile together, which is worse than no option at all.
/// </para>
/// </remarks>
public class StrongTypedIdentityTests
{
    private const string Ordering =
        """
        slices:
          Ordering:
            steps:
              - c: Member/Place Order
                props: { orderId: order-123, total: 12.5 }
              - e: Member/Order Placed
                props: { orderId: order-123 }
              - c: Member/Cancel Order
                props: { orderId: order-123 }
              - e: Member/Order Cancelled
              - v: Member/Order Summary
            tests:
              AnOrderIsCancelled:
                given: [{ e: Member/Order Placed }]
                when: [{ c: Member/Cancel Order }]
                then: [{ e: Member/Order Cancelled }]
        """;

    private static EmlangBoard board(string yaml = Ordering) => EmlangReader.Read(yaml);

    private static ImportedEventModel model(string yaml = Ordering)
        => EmlangImport.ToCurated(board(yaml), "Shop").Model;

    private static (CSharpModelWriter.Output Stubs, GeneratedSpecs Specs) imported(string yaml = Ordering, bool typed = true)
    {
        var imported = model(yaml);
        var ids = typed ? StrongTypedIds.For(imported, CSharpModelWriter.StubNames(imported)) : StrongTypedIds.None;
        var specs = EmlangSpecWriter.Write(board(yaml), imported, "Shop", "Shop.Specs", null, ids);

        return (CSharpModelWriter.Write(imported, "Shop", specs.Additions, perChapter: true, ids), specs);
    }

    [Fact]
    public void each_aggregate_gets_one_identity_type_in_the_root_namespace()
    {
        var identities = imported().Stubs.StubFiles.Single(x => x.Path == "Identities.cs");

        // Marten considers a type a strong-typed identity only when its name ends in Id, so
        // {Aggregate}Id is a requirement rather than a style; Value and the constructor are what
        // Marten, Polecat and Fisher all recognise.
        identities.Content.ShouldContain("public readonly record struct OrderId(Guid Value) : IParsable<OrderId>");

        // In the root namespace, which encloses every chapter's, so no stub needs a using for it
        identities.Content.ShouldContain("namespace Shop;");
    }

    [Fact]
    public void an_identity_carries_what_an_imported_model_actually_does_with_it()
    {
        var identities = imported().Stubs.StubFiles.Single(x => x.Path == "Identities.cs").Content;

        // An HTTP slice posts the command and reads the id back, so a bare struct would be a type
        // the first endpoint breaks on: ToString is the value, IParsable binds a route parameter,
        // and the converter puts the value on the wire rather than an object wrapping it.
        identities.ShouldContain("public override string ToString() => Value.ToString();");
        identities.ShouldContain("public static OrderId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));");
        identities.ShouldContain("[JsonConverter(typeof(OrderId.Converter))]");
        identities.ShouldContain("public sealed class Converter : JsonConverter<OrderId>");
    }

    [Fact]
    public void every_member_that_addresses_the_stream_takes_the_identity_type()
    {
        var stubs = imported().Stubs.AllStubs();

        // The aggregate's own id, the command's {Aggregate}Id, the event's, and the read model's --
        // the last is a correctness claim, not a convenience: a single-stream projection's document
        // id must BE the stream's identity type
        stubs.ShouldContain("public class Order { public OrderId Id { get; set; } }");
        stubs.ShouldContain("public record CancelOrder(OrderId OrderId);");
        stubs.ShouldContain("public record OrderPlaced(OrderId OrderId);");
        stubs.ShouldContain("public record OrderSummary(OrderId Id);");
    }

    [Fact]
    public void the_generated_code_compiles_and_declares_the_identity_type()
    {
        var generated = imported().Stubs;

        CSharpModelWriterTests.compile(generated).ShouldBeEmpty();

        // jasperfx#1004: a Declared claim the store's own projection later agrees with, which is how
        // the two rungs of the model meet on an identity instead of minting a hotspot
        var descriptor = CSharpModelWriterTests.buildAll(generated).Single().Model;
        var order = descriptor.Aggregates.Single(x => x.Type.Name == "Order");
        order.IdentityType.ShouldNotBeNull().Name.ShouldBe("OrderId");
    }

    [Fact]
    public void a_generated_specification_declares_the_identity_as_the_typed_value()
    {
        var code = imported().Specs.AllCode();

        // The half that would otherwise not compile: a Guid local handed to an OrderId member
        code.ShouldContain("var theOrder = new OrderId(Guid.CreateVersion7());");
        code.ShouldContain(".With(x => x.OrderId, theOrder)");
    }

    [Fact]
    public void nothing_changes_without_the_flag()
    {
        var plain = imported(typed: false);

        plain.Stubs.StubFiles.ShouldNotContain(x => x.Path == "Identities.cs");
        plain.Stubs.AllStubs().ShouldContain("public class Order { public Guid Id { get; set; } }");
        plain.Stubs.Definition.ShouldNotContain("IdentifiedBy");
        plain.Specs.AllCode().ShouldContain("var theOrder = Guid.CreateVersion7();");
    }

    [Fact]
    public void a_string_keyed_stream_gets_a_string_identity()
    {
        // A natural key cannot be minted, and the identity type wraps what the stream's id would
        // otherwise have been (bobcat#453) -- so the struct is over a string, and parses as one.
        var board = EventModelersJsonReader.Read(NaturalKey);
        var imported = EmlangImport.ToCurated(board, "Customers", "Customers").Model;
        var ids = StrongTypedIds.For(imported, CSharpModelWriter.StubNames(imported));
        var specs = EmlangSpecWriter.Write(board, imported, "Customers", "Customers.Specs", null, ids);
        var generated = CSharpModelWriter.Write(imported, "Customers", specs.Additions, perChapter: true, ids);

        generated.StubFiles.Single(x => x.Path == "Identities.cs").Content
            .ShouldContain("public readonly record struct CustomerId(string Value) : IParsable<CustomerId>");

        // The command carries the key, now as the identity type, and the spec declares it as one
        generated.AllStubs().ShouldContain("public record RegisterCustomer(CustomerId Id, string Name);");
        specs.AllCode().ShouldContain("""var theCustomer = new CustomerId("customer-1");""");

        CSharpModelWriterTests.compile(generated).ShouldBeEmpty();
    }

    [Fact]
    public void an_aggregate_whose_identity_name_the_model_takes_keeps_a_primitive_one()
    {
        // Two types of one name in one namespace is output that does not build, which is the one
        // thing a scaffolder must never produce. So the aggregate keeps its Guid and the import says
        // so, rather than shadowing the board's own element.
        var yaml =
            """
            slices:
              Ordering:
                steps:
                  - c: Member/Place Order
                  - e: Member/Order Placed
                  - v: Member/Order Id
            """;

        var imported = model(yaml);
        var ids = StrongTypedIds.For(imported, CSharpModelWriter.StubNames(imported));

        ids.IdOf("Order").ShouldBeNull();
        ids.Callouts.ShouldHaveSingleItem().Code.ShouldBe(ImportCalloutCodes.StrongTypedIdNameTaken);

        var generated = CSharpModelWriter.Write(imported, "Shop", null, perChapter: true, ids);
        generated.AllStubs().ShouldContain("public class Order { public Guid Id { get; set; } }");
        CSharpModelWriterTests.compile(generated).ShouldBeEmpty();
    }

    [Fact]
    public void a_command_against_several_streams_takes_each_streams_identity_type()
    {
        // bobcat#444's shape: one {Aggregate}Id per stream, each addressing its own [WriteModel],
        // and each now typed by the stream it addresses -- which is what stops two Guids that
        // mean different streams from being interchangeable.
        var board = EmlangReader.Read(AggregateInferenceTests.Shelter);
        var imported = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var ids = StrongTypedIds.For(imported, CSharpModelWriter.StubNames(imported));
        var specs = EmlangSpecWriter.Write(board, imported, "Shelter", "Shelter.Specs", null, ids);
        var generated = CSharpModelWriter.Write(imported, "Shelter", specs.Additions, perChapter: true, ids);

        generated.AllStubs().ShouldContain(
            "public record AcceptHomeCheckAssignment(HomeCheckId HomeCheckId, VolunteerApplicationId VolunteerApplicationId);");

        var code = specs.AllCode();
        code.ShouldContain("var theHomeCheck = new HomeCheckId(Guid.CreateVersion7());");
        code.ShouldContain(".With(x => x.VolunteerApplicationId, theVolunteerApplication)");

        CSharpModelWriterTests.compile(generated).ShouldBeEmpty();
    }

    // The eventmodelers.ai shape for a stream keyed by a natural key: the identity field is marked,
    // and declared a String (StreamStartIdentityTests' own fixture).
    private const string NaturalKey =
        """
        { "slices": [ { "title": "Register", "aggregates": ["Customer"],
            "commands": [ { "title": "Register Customer", "aggregate": "Customer", "fields": [
              { "name": "name", "type": "String" } ] } ],
            "events": [ { "title": "Customer Registered", "aggregate": "Customer", "fields": [
              { "name": "email", "type": "String", "idAttribute": true },
              { "name": "name", "type": "String" } ] } ],
            "specifications": [ { "title": "Registers",
              "given": [],
              "when": [ { "title": "Register Customer", "type": "COMMAND", "fields": [ { "name": "name", "example": "Ann" } ] } ],
              "then": [ { "title": "Customer Registered", "type": "EVENT", "fields": [ { "name": "name", "example": "Ann" } ] } ] } ] } ] }
        """;
}
