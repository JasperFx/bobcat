using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#453: a command that starts a stream carries no <c>Id</c> unless an example gives it one,
/// because its handler mints the new stream's id. A string identity is the exception: a natural key
/// cannot be minted, so the command carries it and the new stream takes it.
/// </summary>
public class StreamStartIdentityTests
{
    private static string generate(EmlangBoard board, string ns = "Shelter")
    {
        var model = EmlangImport.ToCurated(board, ns, ns).Model;
        return CSharpModelWriter.Write(model, ns, null).AllStubs();
    }

    [Fact]
    public void a_command_that_only_starts_a_stream_gets_no_id()
    {
        var stubs = generate(EmlangReader.Read(AggregateInferenceTests.Shelter));

        stubs.ShouldContain("public record ApplyToVolunteer;");
        stubs.ShouldContain("public record ProposeHomeCheckAppointment;");
        stubs.ShouldContain("public record ProposeFosterHandoverAppointment;");
    }

    [Fact]
    public void a_command_against_an_existing_stream_keeps_its_id()
    {
        var stubs = generate(EmlangReader.Read(AggregateInferenceTests.Shelter));

        stubs.ShouldContain("public record ReviewVolunteerApplication(Guid Id);");
        stubs.ShouldContain("public record ConfirmAppointment(Guid Id);");
    }

    [Fact]
    public void a_command_an_example_gives_an_id_keeps_it()
    {
        var stubs = generate(EmlangReader.Read(
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
    public void a_command_whose_handler_mints_the_id_carries_none()
    {
        generate(EventModelersJsonReader.Read(MintedIdentity), "Applications")
            .ShouldContain("public record SubmitApplication(string Areas);");
    }

    [Fact]
    public void a_string_keyed_stream_is_started_with_the_id_its_command_carries()
    {
        var board = EventModelersJsonReader.Read(NaturalKey);
        var model = EmlangImport.ToCurated(board, "Customers", "Customers").Model;

        CSharpModelWriter.StreamIdentityType(model, "Customer").ShouldBe("string");

        var stubs = generate(board, "Customers");

        // A natural key cannot be minted, so the command carries it, as a string
        stubs.ShouldContain("public record RegisterCustomer(string Id, string Name);");
        stubs.ShouldNotContain("public Guid Id");
    }

    [Fact]
    public void a_guid_keyed_stream_has_a_guid_identity()
    {
        var model = EmlangImport.ToCurated(EventModelersJsonReader.Read(MintedIdentity), "Applications").Model;

        CSharpModelWriter.StreamIdentityType(model, "Application").ShouldBe("Guid");
    }
}
