using System.Reflection;
using Bobcat.EventModel.Emlang;
using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// The additive re-import: without <c>--force</c>, an existing file gains what the board has and it
/// lacks — a slice declaration, a stub type, a specification — and nothing already there changes.
/// Each test imports a smaller board, then the full one, into the same directories, exactly as
/// <c>bobcat import-event-model</c> does.
/// </summary>
public sealed class ImportMergeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bobcat-import-merge-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // the temp folder is the OS's to clear
        }
    }

    private string app => Path.Combine(_root, "Shelter");
    private string specs => Path.Combine(_root, "Shelter.Specs");

    /// <summary>The Shelter board, plus a second event on Confirm appointment and a second example on Cancel appointment.</summary>
    private static readonly string Full = AggregateInferenceTests.Shelter.Replace("\r", "")
        .Replace("      - e: Member / Appointment confirmed\n", "      - e: Member / Appointment confirmed\n      - e: Member / Reminder scheduled\n")
        + "\n      Cancelled before confirming:\n"
        + "        given:\n          - e: Home check appointment proposed\n"
        + "        when:\n          - c: Cancel appointment\n"
        + "        then:\n          - e: Appointment cancelled\n";

    private const string AcceptSteps = "      - c: Volunteer / Accept home check assignment\n      - e: Volunteer / Home check assignment accepted\n";

    private const string AcceptTest = "      Accepted:\n"
                                      + "        given:\n          - e: Home check requested\n          - e: Volunteer approved\n"
                                      + "        when:\n          - c: Accept home check assignment\n"
                                      + "        then:\n          - e: Home check assignment accepted\n";

    /// <summary>The full board less the Accept home check assignment slice and its example.</summary>
    private static string withoutAcceptSlice() => without(Full, AcceptSteps, AcceptTest);

    /// <summary>The full board less the Reminder scheduled event.</summary>
    private static string withoutReminder() => without(Full, "      - e: Member / Reminder scheduled\n");

    /// <summary>The full board less the second Cancel appointment example.</summary>
    private static string withoutSecondCancel() => Full[..Full.IndexOf("\n      Cancelled before confirming:", StringComparison.Ordinal)] + "\n";

    private static string without(string yaml, params string[] parts)
    {
        foreach (var part in parts)
        {
            var normalized = part.Replace("\r", "");
            yaml.ShouldContain(normalized);
            yaml = yaml.Replace(normalized, "");
        }

        return yaml;
    }

    private ImportFileWriter import(string yaml, bool force = false)
    {
        var board = EmlangReader.Read(yaml);
        var result = EmlangImport.ToCurated(board, "Shelter", "Shelter");
        var generatedSpecs = EmlangSpecWriter.Write(result.Board ?? board, result.Model, "Shelter", "Shelter.Specs", "marten");
        var generated = CSharpModelWriter.Write(result.Model, "Shelter", generatedSpecs.Additions, perChapter: true);

        var writer = new ImportFileWriter(force);
        writer.WriteStubs(app, generated.StubFiles);
        writer.WriteDefinitions(app, generated.DefinitionFiles, result.Model);
        writer.WriteSpecs(specs, generatedSpecs.Files);
        return writer;
    }

    private Dictionary<string, string> snapshot()
        => Directory.EnumerateFiles(_root, "*.cs", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllText);

    private string read(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    /// <summary>What <paramref name="after"/> holds beyond <paramref name="before"/>, failing unless the only change is one insertion.</summary>
    private static string insertedOnly(string before, string after)
    {
        var prefix = 0;
        while (prefix < before.Length && before[prefix] == after[prefix]) prefix++;
        var inserted = after.Length - before.Length;
        inserted.ShouldBeGreaterThan(0);
        after[(prefix + inserted)..].ShouldBe(before[prefix..], "everything after the insertion is byte-identical");
        return after.Substring(prefix, inserted);
    }

    /// <summary>Compile every stub and definition the app directory holds, then run the definitions.</summary>
    private IReadOnlyList<EventModelSliceDescriptor> buildApp()
    {
        var trees = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Select(x => CSharpSyntaxTree.ParseText(File.ReadAllText(x), path: x));
        var compilation = CSharpCompilation.Create("Merged" + Guid.NewGuid().ToString("N"), trees, CSharpModelWriterTests.references(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        emitted.Success.ShouldBeTrue("the merged code must compile: "
                                     + string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        return Assembly.Load(stream.ToArray()).GetTypes()
            .Where(t => typeof(EventModelDefinition).IsAssignableFrom(t))
            .SelectMany(t =>
            {
                var builder = new EventModelBuilder();
                ((EventModelDefinition)Activator.CreateInstance(t)!).Configure(builder);
                return builder.Build("Shelter").Slices;
            })
            .ToList();
    }

    [Fact]
    public void a_new_slice_in_an_existing_chapter_is_declared_at_the_end_of_its_definition_and_nothing_else_moves()
    {
        import(withoutAcceptSlice());
        var before = read("Shelter/Features/Volunteering/VolunteeringModel.cs");
        before.ShouldNotContain("AcceptHomeCheckAssignment");

        var writer = import(Full);
        var after = read("Shelter/Features/Volunteering/VolunteeringModel.cs");

        var inserted = insertedOnly(before, after);
        inserted.ShouldContain("model.Command<AcceptHomeCheckAssignment>()");
        writer.Callouts.ShouldContain(x => x.Code == ImportCalloutCodes.MergedSliceDeclaration && x.Slice == "AcceptHomeCheckAssignment");
        // The full board infers a HomeCheck aggregate the smaller one did not, so it is declared beside the slice
        writer.Lines.ShouldContain($"Added 1 slice declaration (AcceptHomeCheckAssignment) and 1 aggregate declaration (HomeCheck) to {Path.Combine("Features", "Volunteering", "VolunteeringModel.cs")}.");
        writer.Files.ShouldContain(x => x.Path.EndsWith("VolunteeringModel.cs") && x.Mode == "merge");

        // Its stub and specification are new files, so they are simply written
        File.Exists(Path.Combine(app, "Features", "Volunteering", "AcceptHomeCheckAssignment.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(specs, "Volunteering", "AcceptHomeCheckAssignment.cs")).ShouldBeTrue();

        buildApp().ShouldContain(x => x.Name == "AcceptHomeCheckAssignment" && x.Chapter == "Volunteering");
    }

    [Fact]
    public void a_new_event_on_an_existing_slice_is_added_to_its_stub_file()
    {
        import(withoutReminder());
        var before = read("Shelter/Features/Booking/ConfirmAppointment.cs");
        before.ShouldNotContain("ReminderScheduled");

        var writer = import(Full);
        var inserted = insertedOnly(before, read("Shelter/Features/Booking/ConfirmAppointment.cs"));

        inserted.ShouldContain("public record ReminderScheduled;");
        writer.Callouts.ShouldContain(x => x.Code == ImportCalloutCodes.MergedType && x.Type == "ReminderScheduled");
        buildApp().ShouldContain(x => x.Name == "ConfirmAppointment");
    }

    [Fact]
    public void a_new_role_on_an_existing_slice_is_reported_and_its_declaration_left_alone()
    {
        import(withoutReminder());
        var before = read("Shelter/Features/Booking/BookingModel.cs");

        var writer = import(Full);

        read("Shelter/Features/Booking/BookingModel.cs").ShouldBe(before);
        var differs = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.SliceDiffersFromBoard);
        differs.Slice.ShouldBe("ConfirmAppointment");
        differs.Message.ShouldContain("Emits(ReminderScheduled)");
    }

    [Fact]
    public void a_slice_opened_by_name_is_not_mistaken_for_the_command_it_names()
    {
        var root = CSharpSyntaxTree.ParseText("""
            using JasperFx.Events.EventModeling;
            public class M : EventModelDefinition
            {
                public override void Configure(EventModelBuilder model)
                {
                    model.Command("Swipe right").Command<SwipeOnDog>().Emits<Swiped>();
                    model.View<Queue>();
                }
            }
            """).GetCompilationUnitRoot();

        ImportMerge.DeclaredSlices(root).ShouldBe(["Swipe right", "Queue"]);
    }

    [Fact]
    public void a_type_whose_members_differ_from_the_board_is_reported_and_left_alone()
    {
        import(withoutReminder());
        var path = Path.Combine(app, "Features", "Booking", "ConfirmAppointment.cs");
        var edited = File.ReadAllText(path).Replace("public record ConfirmAppointment(Guid Id);", "public record ConfirmAppointment(string Id, string Note);");
        edited.ShouldContain("ConfirmAppointment(string Id, string Note)");
        File.WriteAllText(path, edited);

        var writer = import(Full);

        var differs = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.TypeDiffersFromBoard);
        differs.Type.ShouldBe("ConfirmAppointment");
        differs.Message.ShouldContain("the board types Id as Guid, the code as string");

        // The differing type is untouched; the new event still goes in beside it
        var after = File.ReadAllText(path);
        insertedOnly(edited, after).ShouldContain("ReminderScheduled");
        after.ShouldContain("public record ConfirmAppointment(string Id, string Note);");
    }

    [Fact]
    public void members_the_code_added_beyond_the_board_are_not_a_difference()
    {
        import(Full);
        var path = Path.Combine(app, "Features", "Booking", "ConfirmAppointment.cs");
        File.WriteAllText(path, File.ReadAllText(path).Replace("public record ConfirmAppointment(Guid Id);", "public record ConfirmAppointment(Guid Id, DateTimeOffset At);"));

        import(Full).Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.TypeDiffersFromBoard);
    }

    [Fact]
    public void a_new_example_gets_its_specification_in_the_existing_class_and_the_existing_ones_are_untouched()
    {
        import(withoutSecondCancel());
        var path = Path.Combine(specs, "Booking", "CancelAppointment.cs");
        // The user edits the existing specification; the merge must not care
        var edited = File.ReadAllText(path).Replace("[Fact]", "[Fact] // checked by hand");
        File.WriteAllText(path, edited);

        var writer = import(Full);
        var inserted = insertedOnly(edited, File.ReadAllText(path));

        inserted.ShouldContain("public async Task cancelled_before_confirming()");
        inserted.ShouldNotContain("public async Task cancelled()");
        writer.Callouts.ShouldContain(x => x.Code == ImportCalloutCodes.MergedSpec && x.Slice == "CancelAppointment");

        var sources = Directory.EnumerateFiles(specs, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText);
        var model = EmlangImport.ToCurated(EmlangReader.Read(Full), "Shelter", "Shelter").Model;
        EmlangSpecWriter.MissingSpecs(model, sources).ShouldBeEmpty();
    }

    [Fact]
    public void a_specification_class_moved_to_another_file_is_still_where_the_new_example_goes()
    {
        import(withoutSecondCancel());
        var original = Path.Combine(specs, "Booking", "CancelAppointment.cs");
        var moved = Path.Combine(specs, "Booking", "Cancelling.cs");
        File.Move(original, moved);

        import(Full);

        File.Exists(original).ShouldBeFalse();
        File.ReadAllText(moved).ShouldContain("cancelled_before_confirming");
    }

    [Fact]
    public void a_file_that_does_not_parse_is_skipped_with_a_callout()
    {
        import(withoutAcceptSlice());
        var path = Path.Combine(app, "Features", "Volunteering", "VolunteeringModel.cs");
        var broken = File.ReadAllText(path).TrimEnd().TrimEnd('}');
        File.WriteAllText(path, broken);

        var writer = import(Full);

        File.ReadAllText(path).ShouldBe(broken);
        var skipped = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.MergeSkippedUnparseable);
        skipped.File.ShouldBe(path);
        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.MergedSliceDeclaration);
    }

    [Fact]
    public void a_definition_reformatted_by_hand_still_takes_the_new_slice_inside_configure()
    {
        import(withoutAcceptSlice());
        var path = Path.Combine(app, "Features", "Volunteering", "VolunteeringModel.cs");
        // Two-space indentation, a trailing comment, a block namespace: none of it is the generated shape
        var reformatted = File.ReadAllText(path)
            .Replace("namespace Shelter.Volunteering;", "namespace Shelter.Volunteering\n{")
            .Replace("    ", "  ")
            .TrimEnd() + "\n// the end\n}\n";
        File.WriteAllText(path, reformatted);

        import(Full);

        var inserted = insertedOnly(reformatted, File.ReadAllText(path));
        inserted.ShouldContain("\n    model.Command<AcceptHomeCheckAssignment>()");
        buildApp().ShouldContain(x => x.Name == "AcceptHomeCheckAssignment");
    }

    [Fact]
    public void a_type_the_user_moved_to_another_file_is_not_declared_again()
    {
        import(withoutReminder());
        File.WriteAllText(Path.Combine(app, "Features", "Booking", "Reminders.cs"),
            "using System;\n\nnamespace Shelter.Booking;\n\npublic record ReminderScheduled(DateTimeOffset At);\n");

        var writer = import(Full);

        read("Shelter/Features/Booking/ConfirmAppointment.cs").ShouldNotContain("ReminderScheduled");
        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.MergedType);
        buildApp();
    }

    [Fact]
    public void importing_twice_changes_nothing_the_second_time()
    {
        import(withoutAcceptSlice());
        import(Full);
        var first = snapshot();

        var writer = import(Full);

        snapshot().ShouldBe(first);
        writer.Callouts.ShouldNotContain(x => x.Code.StartsWith("merged-"));
        writer.Files.ShouldAllBe(x => x.Mode == "kept");
        writer.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void force_still_overwrites_every_file_with_the_fresh_import()
    {
        import(withoutReminder());
        var path = Path.Combine(app, "Features", "Booking", "ConfirmAppointment.cs");
        File.WriteAllText(path, "// my edits\n");

        var writer = import(Full, force: true);

        File.ReadAllText(path).ShouldContain("public record ReminderScheduled;");
        File.ReadAllText(path).ShouldNotContain("my edits");
        writer.Files.ShouldAllBe(x => x.Mode == "write");
        writer.Callouts.ShouldBeEmpty();
    }

    [Fact]
    public void the_merge_callouts_carry_their_type_in_the_json()
    {
        var json = ImportJson.Write("Shelter",
            [new ImportCallout("", ImportCalloutCodes.MergedType, [], "Added X", null) { File = "/a/B.cs", Type = "X" }], []);

        json.ShouldContain("\"code\": \"merged-type\"");
        json.ShouldContain("\"type\": \"X\"");
        json.ShouldContain("\"file\": \"/a/B.cs\"");
    }
}
