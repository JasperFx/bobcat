using System.Runtime.CompilerServices;
using Bobcat.EventModel.Emlang;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#851: the import writes typed stubs from the board's field model, and the generated definition declares
/// the same fields with <c>model.Shape&lt;T&gt;()</c>, so the Declared rung carries them. The board is the
/// <c>typed.json</c> fixture the docs show the import of.
/// </summary>
public class TypedStubTests
{
    private static string boards([CallerFilePath] string path = "")
        => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "Import", "boards");

    private static ImportedEventModel imported()
        => EmlangImport.ToCurated(EventModelersJsonReader.Read(File.ReadAllText(Path.Combine(boards(), "typed.json"))), "Clinic", "Clinic").Model;

    private static CSharpModelWriter.Output generated(bool perChapter = false)
        => CSharpModelWriter.Write(imported(), "Clinic", null, perChapter);

    [Fact]
    public void the_reader_keeps_every_declared_field_with_its_type_and_flags()
    {
        var board = EventModelersJsonReader.Read(File.ReadAllText(Path.Combine(boards(), "typed.json")));
        var command = board.Chapters[0].Steps.Single(x => x.Kind == EmlangElementKind.Command);

        command.Fields.Select(x => x.Name).ShouldBe(["appointmentId", "patientEmail", "slots", "notes", "window"]);
        command.Fields.Single(x => x.Name == "appointmentId").ShouldSatisfyAllConditions(
            f => f.Type.ShouldBe("UUID"), f => f.Identity.ShouldBeTrue(), f => f.Generated.ShouldBeTrue());
        command.Fields.Single(x => x.Name == "patientEmail").Pii.ShouldBeTrue();
        command.Fields.Single(x => x.Name == "slots").List.ShouldBeTrue();
        command.Fields.Single(x => x.Name == "notes").Optional.ShouldBeTrue();
        command.Fields.Single(x => x.Name == "window").Subfields.Select(x => (x.Name, x.Type)).ShouldBe([("day", "Date"), ("from", "DateTime"), ("flexible", "Boolean")]);
        command.Description.ShouldBe("Books one visit for a patient.");
    }

    [Fact]
    public void the_command_stub_is_typed_from_the_board()
    {
        var stubs = generated().AllStubs();

        // UUID → Guid, Int list → List<int>, optional String → string?, Custom → a nested record named for the field
        stubs.ShouldContain("public record BookAppointment([Generated] Guid AppointmentId, [Pii] string PatientEmail, List<int> Slots, string? Notes, Window Window);");
        stubs.ShouldContain("public record Window(DateOnly Day, DateTimeOffset From, bool Flexible);");
        stubs.ShouldContain("public record AppointmentBooked(Guid AppointmentId, [Pii] string PatientEmail, [Generated] DateTimeOffset BookedAt, decimal? Deposit);");
        stubs.ShouldContain("public record AppointmentList(Guid AppointmentId, [Pii] string Email, long Visits);");
    }

    [Fact]
    public void an_optional_field_turns_nullable_reference_types_on_for_its_file()
    {
        var file = generated().StubFiles.Single(x => x.Path.EndsWith("BookAppointment.cs"));

        file.Content.ShouldContain("#nullable enable");
        file.Content.ShouldContain("using JasperFx;");
        file.Content.ShouldContain("using System.Collections.Generic;");
    }

    [Fact]
    public void descriptions_become_doc_comments()
    {
        var stubs = generated().AllStubs();

        stubs.ShouldContain("/// Books one visit for a patient.");
        stubs.ShouldContain("/// Every booked visit, for the front desk.");
    }

    [Fact]
    public void the_generated_code_compiles_with_no_diagnostics()
    {
        CSharpModelWriterTests.compile(generated()).ShouldBeEmpty();
        CSharpModelWriterTests.compile(generated(perChapter: true)).ShouldBeEmpty();
    }

    [Fact]
    public void the_definition_declares_the_fields_and_the_descriptions()
    {
        var definition = generated().Definition;

        definition.ShouldContain("model.Shape<BookAppointment>()");
        definition.ShouldContain(""".Field("AppointmentId").OfType<Guid>().Identity().Generated()""");
        definition.ShouldContain(""".Field("PatientEmail").OfType<string>().Pii()""");
        definition.ShouldContain(""".Field("Slots").OfType<int>().Many()""");
        definition.ShouldContain(""".Field("Notes").OfType<string>().Optional()""");
        definition.ShouldContain(""".Field("Window").OfType<Window>()""");
        definition.ShouldContain("model.Shape<Window>()");
        definition.ShouldContain(""".Field("Email").OfType<string>().Pii().From<AppointmentBooked>("PatientEmail")""");
        definition.ShouldContain(""".Describe("A patient books a visit from the schedule page.")""");
        definition.ShouldContain(""".Describe<BookAppointment>("Books one visit for a patient.")""");
    }

    [Fact]
    public void the_declared_shapes_round_trip_through_the_compiled_definition()
    {
        var model = imported();
        var output = CSharpModelWriter.Write(model, "Clinic", null, perChapter: false);

        var fromCode = CSharpModelWriterTests.build(output);
        var fromModel = ImportedModelMapper.ToDescriptor(model, output.Layout, output.Shapes);

        describe(fromCode).ShouldBe(describe(fromModel));
        fromCode.Shapes.Select(x => x.Type.Name).ShouldBe(["Appointment", "BookAppointment", "Window", "AppointmentBooked", "AppointmentList"], ignoreOrder: true);

        var command = fromCode.Slices.Single(x => x.Name == "BookAppointment");
        command.Description.ShouldBe("A patient books a visit from the schedule page.");
        command.ElementDescriptions.ShouldContain(x => x.Type.Name == "BookAppointment" && x.Text == "Books one visit for a patient.");
        fromModel.Slices.Single(x => x.Name == "BookAppointment").Description.ShouldBe(command.Description);

        // A mapping the board gives by field name is resolved to the consumed event that declares the field
        var email = fromCode.ShapeOf(fromCode.Shapes.Single(x => x.Type.Name == "AppointmentList").Type)!.Find("Email")!;
        email.Mapping.ShouldNotBeNull().Field.ShouldBe("PatientEmail");
        email.Mapping!.Source.Name.ShouldBe("AppointmentBooked");
    }

    [Fact]
    public void a_compound_identity_is_a_record_struct_of_its_members()
    {
        const string board =
            """
            { "slices": [ { "title": "Open Encounter", "chapter": "Visits", "sliceType": "STATE_CHANGE", "aggregates": ["Encounter"],
                "commands": [ { "title": "Open Encounter", "fields": [
                  { "name": "patientId", "type": "UUID", "idAttribute": true },
                  { "name": "visitNumber", "type": "Int", "idAttribute": true },
                  { "name": "reason", "type": "String" } ] } ],
                "events": [ { "title": "Encounter Opened", "aggregate": "Encounter", "fields": [
                  { "name": "patientId", "type": "UUID", "idAttribute": true },
                  { "name": "visitNumber", "type": "Int", "idAttribute": true } ] } ],
                "readmodels": [], "specifications": [] } ] }
            """;
        var model = EmlangImport.ToCurated(EventModelersJsonReader.Read(board), "Clinic", "Clinic").Model;

        CSharpModelWriter.IdentityComponents(model, "Encounter").ShouldBe([("PatientId", "Guid"), ("VisitNumber", "int")]);

        var ids = StrongTypedIds.For(model, CSharpModelWriter.StubNames(model));
        ids.Components("EncounterId").ShouldNotBeNull().Count.ShouldBe(2);
        ids.Inner("EncounterId").ShouldBeNull();

        var output = CSharpModelWriter.Write(model, "Clinic", null, perChapter: false, ids);
        var identities = output.StubFiles.Single(x => x.Path == "Identities.cs").Content;
        identities.ShouldContain("public readonly record struct EncounterId(Guid PatientId, int VisitNumber)");
        identities.ShouldNotContain("IParsable<EncounterId>");

        // The components stay primitive on the command and the event; the aggregate's Id is the compound
        output.AllStubs().ShouldContain("public record OpenEncounter(Guid PatientId, int VisitNumber, string Reason);");
        output.AllStubs().ShouldContain("public EncounterId Id { get; set; }");

        CSharpModelWriterTests.compile(output).ShouldBeEmpty();
        CSharpModelWriterTests.build(output).Aggregates.Single().IdentityType!.Name.ShouldBe("EncounterId");
    }

    [Fact]
    public void a_merged_field_callout_carries_its_field_in_the_json()
    {
        var callout = new ImportCallout("", ImportCalloutCodes.MergedField, [], "Added", null) { Type = "BookAppointment", Field = "Notes", File = "x.cs" };

        var json = ImportJson.Write("Clinic", [callout], []);

        json.ShouldContain("\"code\": \"merged-field\"");
        json.ShouldContain("\"type\": \"BookAppointment\"");
        json.ShouldContain("\"field\": \"Notes\"");
    }

    private static IReadOnlyList<string> describe(EventModelDescriptor descriptor)
        => descriptor.Shapes
            .OrderBy(x => x.Type.Name, StringComparer.Ordinal)
            .Select(shape => $"{shape.Type.Name}: " + string.Join(", ", shape.Fields.Select(f =>
                $"{f.Name}:{f.Type.Name}{(f.Cardinality == FieldCardinality.Many ? "[]" : "")}{(f.Optional ? "?" : "")}"
                + $"{(f.Identity ? " id" : "")}{(f.Generated ? " generated" : "")}{(f.Pii ? " pii" : "")}"
                + $"{(f.Mapping is null ? "" : $" from {f.Mapping.Source.Name}.{f.Mapping.Field}")}")))
            .ToList();
}
