using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#453: a command that starts a stream carries no <c>Id</c> unless a specification needs it,
/// because its handler mints the new stream's id. A string identity is the exception: a natural key
/// cannot be minted, so the command carries it and the new stream takes it.
/// </summary>
public class StreamStartIdentityTests
{
    private static (string Stubs, string Specs) generate(EmlangBoard board, string ns = "Shelter")
    {
        var model = EmlangImport.ToCurated(board, ns, ns).Model;
        var specs = EmlangSpecWriter.Write(board, model, ns);
        return (CSharpModelWriter.Write(model, ns, specs.Additions).AllStubs(), specs.AllCode());
    }

    [Fact]
    public void a_command_that_only_starts_a_stream_gets_no_id()
    {
        var (stubs, _) = generate(EmlangReader.Read(AggregateInferenceTests.Shelter));

        stubs.ShouldContain("public record ApplyToVolunteer;");
        stubs.ShouldContain("public record ProposeHomeCheckAppointment;");
        stubs.ShouldContain("public record ProposeFosterHandoverAppointment;");
    }

    [Fact]
    public void a_command_against_an_existing_stream_keeps_its_id()
    {
        var (stubs, _) = generate(EmlangReader.Read(AggregateInferenceTests.Shelter));

        stubs.ShouldContain("public record ReviewVolunteerApplication(Guid Id);");
        stubs.ShouldContain("public record ConfirmAppointment(Guid Id);");
    }

    [Fact]
    public void a_command_an_example_gives_an_id_keeps_it()
    {
        var (stubs, _) = generate(EmlangReader.Read(
            """
            slices:
              Apply:
                steps:
                  - c: Apply to volunteer
                  - e: Volunteer application submitted
                tests:
                  Submitted:
                    when:
                      - c: Apply to volunteer
                        props:
                          id: application-1
                    then:
                      - e: Volunteer application submitted
            """));

        stubs.ShouldContain("public record ApplyToVolunteer(Guid Id);");
    }

    private const string MintedIdentity =
        """
        { "slices": [ { "title": "Apply", "aggregates": ["Application"],
            "commands": [ { "title": "Submit Application", "aggregate": "Application", "fields": [
              { "name": "areas", "type": "String" } ] } ],
            "events": [ { "title": "Application Submitted", "aggregate": "Application", "fields": [
              { "name": "applicationId", "type": "UUID", "idAttribute": true },
              { "name": "areas", "type": "String" } ] } ],
            "specifications": [ { "title": "Submits",
              "given": [],
              "when": [ { "title": "Submit Application", "type": "COMMAND", "fields": [ { "name": "areas", "example": "dogs" } ] } ],
              "then": [ { "title": "Application Submitted", "type": "EVENT", "fields": [ { "name": "areas", "example": "dogs" } ] } ] } ] } ] }
        """;

    [Fact]
    public void the_spec_reads_a_started_streams_id_from_the_act_when_the_command_cannot_carry_it()
    {
        var (stubs, specs) = generate(EventModelersJsonReader.Read(MintedIdentity), "Applications");
        var code = EmlangSpecWriterTests.unindented(specs);

        stubs.ShouldContain("public record SubmitApplication(string Areas);");

        // No value the spec declares can reach a handler that mints the id, so none is declared up front
        specs.ShouldNotContain("GivenNoEventsFor");
        specs.ShouldNotContain("Guid.CreateVersion7()");
        code.ShouldContain(EmlangSpecWriterTests.unindented(
            """
            await WhenReceived(Specify<SubmitApplication>().With(x => x.Areas, "dogs"));

            var theApplication = TheStartedStream<Application>();
            """));
        code.ShouldContain("ThenStreamIsStartedWithEvents<Application>(Specify<ApplicationSubmitted>()");
        code.ShouldContain(".With(x => x.ApplicationId, theApplication)");
    }

    [Fact]
    public void a_started_streams_id_nothing_expects_is_not_read_back()
    {
        var (_, specs) = generate(EventModelersJsonReader.Read(MintedIdentity.Replace(
            "{ \"name\": \"applicationId\", \"type\": \"UUID\", \"idAttribute\": true },", "")), "Applications");

        specs.ShouldNotContain("TheStartedStream");
        specs.ShouldContain("ThenStreamIsStartedWithEvents<Application>(");
    }

    [Fact]
    public void a_command_that_carries_the_streams_identity_still_arranges_the_empty_stream()
    {
        // The model names the identity on the command, so the spec knows the id before the act
        var (_, specs) = generate(EventModelersJsonReader.Read(MintedIdentity.Replace(
            "{ \"name\": \"areas\", \"type\": \"String\" } ] } ],\n    \"events\"",
            "{ \"name\": \"applicationId\", \"type\": \"UUID\", \"idAttribute\": true }, { \"name\": \"areas\", \"type\": \"String\" } ] } ],\n    \"events\"")),
            "Applications");

        specs.ShouldContain("await GivenNoEventsFor<Application>(theApplication);");
        specs.ShouldNotContain("TheStartedStream");
    }

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

    [Fact]
    public void a_string_keyed_stream_is_started_with_the_id_its_command_carries()
    {
        var board = EventModelersJsonReader.Read(NaturalKey);
        var model = EmlangImport.ToCurated(board, "Customers", "Customers").Model;

        CSharpModelWriter.StreamIdentityType(model, "Customer").ShouldBe("string");

        var (stubs, specs) = generate(board, "Customers");

        // A natural key cannot be minted, so the command carries it, as a string
        stubs.ShouldContain("public record RegisterCustomer(string Id, string Name);");
        stubs.ShouldNotContain("public Guid Id");
        specs.ShouldContain("await GivenNoEventsFor<Customer>(theCustomer);");
        specs.ShouldContain(".With(x => x.Id, theCustomer)");
        specs.ShouldNotContain("TheStartedStream");
    }

    [Fact]
    public void a_guid_keyed_stream_has_a_guid_identity()
    {
        var model = EmlangImport.ToCurated(EventModelersJsonReader.Read(MintedIdentity), "Applications").Model;

        CSharpModelWriter.StreamIdentityType(model, "Application").ShouldBe("Guid");
    }
}
