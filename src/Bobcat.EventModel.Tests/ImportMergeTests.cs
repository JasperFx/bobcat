using System.Reflection;
using Bobcat.EventModel.Emlang;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// The additive re-import: without <c>--force</c>, an existing file gains what the board has and it
/// lacks — a slice declaration or a stub type — and nothing already there changes.
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

    /// <summary>The full board plus a second new event on Confirm appointment, Appointment rescheduled.</summary>
    private static readonly string FullWithReschedule = Full.Replace("      - e: Member / Reminder scheduled\n",
        "      - e: Member / Reminder scheduled\n      - e: Member / Appointment rescheduled\n");

    /// <summary>The Volunteering chapter alone: the board with the whole Booking chapter gone.</summary>
    private static string volunteeringOnly() => Full[..Full.IndexOf("  Booking:", StringComparison.Ordinal)];

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
        var generated = CSharpModelWriter.Write(result.Model, "Shelter", null, perChapter: true);

        var writer = new ImportFileWriter(force);
        writer.WriteStubs(app, generated.StubFiles);
        writer.WriteDefinitions(app, generated.DefinitionFiles, result.Model);
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
        // The full board infers a HomeCheck aggregate the smaller one did not, so it is declared beside the slice,
        // and both new types get their field shapes (bobcat#851)
        writer.Lines.ShouldContain($"Added 1 slice declaration (AcceptHomeCheckAssignment) and 1 aggregate declaration (HomeCheck) and 2 field shapes (HomeCheck, AcceptHomeCheckAssignment) to {Path.Combine("Features", "Volunteering", "VolunteeringModel.cs")}.");
        inserted.ShouldContain("model.Shape<AcceptHomeCheckAssignment>()");
        writer.Callouts.ShouldContain(x => x.Code == ImportCalloutCodes.MergedShape && x.Type == "HomeCheck");
        writer.Files.ShouldContain(x => x.Path.EndsWith("VolunteeringModel.cs") && x.Mode == "merge");

        // Its stub is a new file, so it is simply written
        File.Exists(Path.Combine(app, "Features", "Volunteering", "AcceptHomeCheckAssignment.cs")).ShouldBeTrue();

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

    /// <summary>
    /// Compile the app directory and assemble its definitions the way a host does: each through
    /// <see cref="EventModelDefinitionSource"/>, folded into one model per name. Statements that name the same
    /// slice become one slice here, which <see cref="buildApp"/>'s raw per-definition list does not show.
    /// </summary>
    private async Task<EventModelDescriptor> buildModel()
    {
        var trees = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Select(x => CSharpSyntaxTree.ParseText(File.ReadAllText(x), path: x));
        var compilation = CSharpCompilation.Create("Merged" + Guid.NewGuid().ToString("N"), trees, CSharpModelWriterTests.references(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        compilation.Emit(stream).Success.ShouldBeTrue("the merged code must compile");

        var services = new ServiceCollection().BuildServiceProvider();
        var descriptors = new List<EventModelDescriptor>();
        foreach (var type in Assembly.Load(stream.ToArray()).GetTypes().Where(t => typeof(EventModelDefinition).IsAssignableFrom(t)))
        {
            var source = EventModelDefinitionSource.For((EventModelDefinition)Activator.CreateInstance(type)!);
            descriptors.Add((await source.TryCreateAsync(services, TestContext.Current.CancellationToken))!);
        }

        return EventModelDescriptor.GroupByName(descriptors).ShouldHaveSingleItem();
    }

    [Fact]
    public void a_new_role_on_an_existing_slice_is_added_by_a_statement_that_reopens_it_and_its_declaration_is_untouched()
    {
        import(withoutReminder());
        var before = read("Shelter/Features/Booking/BookingModel.cs");

        var writer = import(Full);

        // The only change is one statement at the end of Configure; the declaration itself is byte-identical
        insertedOnly(before, read("Shelter/Features/Booking/BookingModel.cs"))
            .ShouldBe("\n        model.Slice(\"ConfirmAppointment\").Emits<ReminderScheduled>();\n");

        var merged = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.MergedSliceRoles);
        merged.Slice.ShouldBe("ConfirmAppointment");
        merged.Roles.ShouldBe(["Emits(ReminderScheduled)"]);
        merged.File.ShouldBe(Path.Combine(app, "Features", "Booking", "BookingModel.cs"));
        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.SliceDiffersFromBoard);
        writer.Lines.ShouldContain($"Added 1 role (ConfirmAppointment: Emits(ReminderScheduled)) to {Path.Combine("Features", "Booking", "BookingModel.cs")}.");
    }

    [Fact]
    public async Task the_reopened_slice_is_one_slice_in_the_model_with_the_new_role_and_nothing_to_disagree_about()
    {
        import(withoutReminder());
        import(Full);

        var model = await buildModel();

        var confirm = model.Slices.Where(x => x.Name == "ConfirmAppointment").ShouldHaveSingleItem();
        confirm.EmittedEvents.Select(x => x.Name).ShouldBe(["AppointmentConfirmed", "ReminderScheduled"]);
        confirm.Pattern.ShouldBe(SlicePattern.Command);
        confirm.Chapter.ShouldBe("Booking");
        confirm.AggregateTypes.Select(x => x.Name).ShouldBe(["Appointment"]);
        confirm.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.SourceDisagreement);
    }

    [Fact]
    public void a_role_already_declared_by_either_statement_is_not_added_again()
    {
        import(withoutReminder());
        import(Full);
        var first = snapshot();

        var writer = import(Full);

        snapshot().ShouldBe(first);
        writer.Callouts.ShouldNotContain(x => x.Code.StartsWith("merged-"));

        // Folded into the declaration by hand, with the reopening statement deleted: still declared
        var path = Path.Combine(app, "Features", "Booking", "BookingModel.cs");
        var folded = File.ReadAllText(path)
            .Replace("\n        model.Slice(\"ConfirmAppointment\").Emits<ReminderScheduled>();\n", "\n")
            .Replace(".Emits<AppointmentConfirmed>()", ".Emits<AppointmentConfirmed>().Emits<ReminderScheduled>()");
        folded.ShouldNotContain("model.Slice(\"ConfirmAppointment\")");
        File.WriteAllText(path, folded);

        import(Full).Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.MergedSliceRoles);
        File.ReadAllText(path).ShouldBe(folded);
    }

    [Fact]
    public void several_new_roles_on_one_slice_are_one_reopening_statement()
    {
        import(withoutReminder());
        var before = read("Shelter/Features/Booking/BookingModel.cs");

        var writer = import(FullWithReschedule);

        insertedOnly(before, read("Shelter/Features/Booking/BookingModel.cs")).ShouldBe("""


                    model.Slice("ConfirmAppointment")
                        .Emits<ReminderScheduled>()
                        .Emits<AppointmentRescheduled>();

            """.Replace("\r", "")[1..]);
        writer.Callouts.Single(x => x.Code == ImportCalloutCodes.MergedSliceRoles).Roles
            .ShouldBe(["Emits(ReminderScheduled)", "Emits(AppointmentRescheduled)"]);
        buildApp().Count(x => x.Name == "ConfirmAppointment").ShouldBe(2, "two statements, which the model folds into one slice");
    }

    [Fact]
    public async Task a_slice_whose_declaration_names_its_own_chapter_is_reopened_in_that_chapter()
    {
        import(withoutReminder());
        var path = Path.Combine(app, "Features", "Booking", "BookingModel.cs");
        File.WriteAllText(path, File.ReadAllText(path).Replace("model.Command<ConfirmAppointment>()", "model.Command<ConfirmAppointment>().InChapter(\"Confirming\")"));

        import(Full);

        File.ReadAllText(path).ShouldContain("""
                    model.Slice("ConfirmAppointment")
                        .InChapter("Confirming")
                        .Emits<ReminderScheduled>();
            """.Replace("\r", ""));

        // The reopening statement would otherwise take Booking from the definition, and contradict the declaration
        var confirm = (await buildModel()).Slices.Single(x => x.Name == "ConfirmAppointment");
        confirm.Chapter.ShouldBe("Confirming");
        confirm.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.SourceDisagreement);
    }

    [Fact]
    public void a_new_role_on_a_slice_declared_in_another_file_is_reported_with_the_call_to_add()
    {
        import(withoutReminder());
        var booking = Path.Combine(app, "Features", "Booking", "BookingModel.cs");
        var text = File.ReadAllText(booking);
        var declaration = text[text.IndexOf("        model.Command<ConfirmAppointment>()", StringComparison.Ordinal)..];
        declaration = declaration[..(declaration.IndexOf(";\n", StringComparison.Ordinal) + 1)];
        File.WriteAllText(booking, text.Replace(declaration, ""));
        var moved = Path.Combine(app, "Features", "Booking", "Confirming.cs");
        File.WriteAllText(moved, $$"""
            using JasperFx.Events.EventModeling;

            namespace Shelter.Booking;

            public class ConfirmingModel : EventModelDefinition
            {
                public override void Configure(EventModelBuilder model)
                {
                    model.InChapter("Booking");
            {{declaration}}
                }
            }
            """);
        var before = snapshot();

        var writer = import(Full);

        File.ReadAllText(moved).ShouldBe(before[moved]);
        File.ReadAllText(booking).ShouldBe(before[booking]);
        var differs = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.SliceDiffersFromBoard);
        differs.Slice.ShouldBe("ConfirmAppointment");
        differs.Roles.ShouldBe(["Emits(ReminderScheduled)"]);
        differs.Fix.ShouldBe("Add .Emits<ReminderScheduled>() to the declaration of slice ConfirmAppointment in Confirming.cs.");
    }

    [Fact]
    public void a_changed_aggregate_is_reported_with_the_edit_to_make_and_the_flag_that_keeps_the_code()
    {
        import(Full);
        var path = Path.Combine(app, "Features", "Booking", "BookingModel.cs");
        var edited = File.ReadAllText(path).Replace("model.Command<ConfirmAppointment>()", "model.Command<ConfirmAppointment>().Against(\"Visit\")");
        File.WriteAllText(path, edited);

        var writer = import(Full);

        File.ReadAllText(path).ShouldBe(edited);
        var differs = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.SliceAggregateDiffers);
        differs.Slice.ShouldBe("ConfirmAppointment");
        differs.Aggregates.ShouldBe(["Appointment"]);
        differs.Message.ShouldContain("The board says slice ConfirmAppointment decides against Appointment; its declaration in BookingModel.cs says it decides against Visit.");
        differs.Fix.ShouldBe("In BookingModel.cs, give slice ConfirmAppointment nothing (the chapter's ForAggregate default, Appointment, applies) in place of .Against(\"Visit\").");
        differs.Override.ShouldBe("--aggregate ConfirmAppointment=Visit");
    }

    [Fact]
    public void a_changed_trigger_is_reported_and_a_trigger_the_board_does_not_state_is_not()
    {
        import(Full);
        var path = Path.Combine(app, "Features", "Booking", "BookingModel.cs");
        var edited = File.ReadAllText(path)
            .Replace("model.Automation(\"ProposeHomeCheckAppointment\")\n            .TriggeredBy(TriggerKind.MessageHandler)",
                "model.Automation(\"ProposeHomeCheckAppointment\")\n            .TriggeredBy(TriggerKind.JobScheduler)")
            .Replace("model.Command<ConfirmAppointment>()", "model.Command<ConfirmAppointment>().TriggeredBy(TriggerKind.Http)");
        edited.ShouldContain("TriggerKind.JobScheduler");
        File.WriteAllText(path, edited);

        var writer = import(Full);

        File.ReadAllText(path).ShouldBe(edited);
        var differs = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.SliceTriggerDiffers);
        differs.Slice.ShouldBe("ProposeHomeCheckAppointment");
        differs.Message.ShouldContain("triggered by MessageHandler; its declaration in BookingModel.cs says JobScheduler");
        differs.Fix.ShouldBe("Replace .TriggeredBy(TriggerKind.JobScheduler) with .TriggeredBy(TriggerKind.MessageHandler) in the declaration of slice ProposeHomeCheckAppointment in BookingModel.cs.");
    }

    [Fact]
    public void a_changed_pattern_is_reported()
    {
        import(Full);
        var path = Path.Combine(app, "Features", "Booking", "BookingModel.cs");
        var edited = File.ReadAllText(path).Replace("model.Automation(\"ProposeFosterHandoverAppointment\")", "model.Command(\"ProposeFosterHandoverAppointment\")");
        edited.ShouldContain("model.Command(\"ProposeFosterHandoverAppointment\")");
        File.WriteAllText(path, edited);

        var writer = import(Full);

        File.ReadAllText(path).ShouldBe(edited);
        var differs = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.SlicePatternDiffers);
        differs.Slice.ShouldBe("ProposeFosterHandoverAppointment");
        differs.Fix.ShouldBe("In BookingModel.cs, open it with model.Automation(\"ProposeFosterHandoverAppointment\") in place of model.Command(\"ProposeFosterHandoverAppointment\"), or keep Command if the code is right.");
        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.SliceAggregateDiffers);
    }

    [Fact]
    public void a_role_the_board_no_longer_gives_is_reported_and_never_removed()
    {
        import(Full);
        var before = snapshot();

        var writer = import(withoutReminder());

        snapshot().ShouldBe(before);
        var removed = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.SliceRoleRemovedOnBoard);
        removed.Slice.ShouldBe("ConfirmAppointment");
        removed.Roles.ShouldBe(["Emits(ReminderScheduled)"]);
        removed.Fix.ShouldBe("Delete .Emits<ReminderScheduled>() from the declaration of slice ConfirmAppointment in BookingModel.cs.");
    }

    [Fact]
    public void a_slice_the_board_no_longer_has_is_reported_and_never_removed()
    {
        import(Full);
        var before = snapshot();

        var writer = import(withoutAcceptSlice());

        // Without that slice the board infers another aggregate for RequestHomeCheck, whose stub is a new file;
        // every file that was there is as it was, except that the definition gains that new type's field shape
        // (bobcat#851) and nothing else
        var now = snapshot();
        foreach (var (path, content) in before)
        {
            if (path.EndsWith("VolunteeringModel.cs", StringComparison.Ordinal))
            {
                insertedOnly(content, now[path]).ShouldContain("model.Shape<Application>()");
                continue;
            }

            now[path].ShouldBe(content, path);
        }

        var removed = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.SliceRemovedOnBoard);
        removed.Slice.ShouldBe("AcceptHomeCheckAssignment");
        removed.File.ShouldBe(Path.Combine(app, "Features", "Volunteering", "VolunteeringModel.cs"));
        removed.Fix.ShouldStartWith("Delete the statement declaring slice AcceptHomeCheckAssignment from VolunteeringModel.cs (line ");
    }

    [Fact]
    public void the_slices_of_a_chapter_the_board_no_longer_has_are_reported_from_its_definition()
    {
        import(Full);
        var before = snapshot();

        var writer = import(volunteeringOnly());

        snapshot().ShouldBe(before);
        writer.Callouts.Where(x => x.Code == ImportCalloutCodes.SliceRemovedOnBoard).Select(x => x.Slice).ShouldBe(
            ["ProposeHomeCheckAppointment", "ProposeFosterHandoverAppointment", "ConfirmAppointment", "CancelAppointment"]);
        writer.Callouts.Where(x => x.Code == ImportCalloutCodes.SliceRemovedOnBoard)
            .ShouldAllBe(x => x.File == Path.Combine(app, "Features", "Booking", "BookingModel.cs"));
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

        // bobcat#851: per field, not per type. Id is typed differently; Note is a member the board does not give
        var differs = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.FieldDiffersFromBoard);
        differs.Type.ShouldBe("ConfirmAppointment");
        differs.Field.ShouldBe("Id");
        differs.Message.ShouldContain("The board types ConfirmAppointment.Id as Guid; the code in ConfirmAppointment.cs types it as string");
        var extra = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.FieldRemovedOnBoard);
        extra.Field.ShouldBe("Note");
        extra.Message.ShouldContain("if you added it by hand, leave it");
        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.TypeDiffersFromBoard);

        // The differing type is untouched; the new event still goes in beside it
        var after = File.ReadAllText(path);
        insertedOnly(edited, after).ShouldContain("ReminderScheduled");
        after.ShouldContain("public record ConfirmAppointment(string Id, string Note);");
    }

    [Fact]
    public void a_member_the_code_added_to_a_stub_is_never_a_difference_and_never_removed()
    {
        import(Full);
        var path = Path.Combine(app, "Features", "Booking", "ConfirmAppointment.cs");
        File.WriteAllText(path, File.ReadAllText(path).Replace("public record ConfirmAppointment(Guid Id);", "public record ConfirmAppointment(Guid Id, DateTimeOffset At);"));

        var writer = import(Full);

        // The stub cannot tell a hand-added member from one the board removed, so it says so and leaves it (bobcat#851)
        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.FieldDiffersFromBoard);
        var removed = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.FieldRemovedOnBoard);
        removed.Type.ShouldBe("ConfirmAppointment");
        removed.Field.ShouldBe("At");
        File.ReadAllText(path).ShouldContain("public record ConfirmAppointment(Guid Id, DateTimeOffset At);");
    }

    [Fact]
    public void a_member_the_code_added_to_a_type_with_behaviour_is_not_reported_at_all()
    {
        import(Full);
        var path = Path.Combine(app, "Features", "Booking", "ConfirmAppointment.cs");
        File.WriteAllText(path, File.ReadAllText(path).Replace("public record ConfirmAppointment(Guid Id);",
            "public record ConfirmAppointment(Guid Id, DateTimeOffset At) { public bool IsLate => At < DateTimeOffset.UtcNow; }"));

        var writer = import(Full);

        // Once the type has grown behaviour it is the code's, and its extra members are nobody's business
        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.FieldRemovedOnBoard && x.Type == "ConfirmAppointment");
        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.FieldDiffersFromBoard && x.Type == "ConfirmAppointment");
    }

    [Fact]
    public void a_new_board_field_is_appended_to_a_type_that_is_still_a_stub()
    {
        import(Full);
        var path = Path.Combine(app, "Features", "Booking", "ConfirmAppointment.cs");
        var before = File.ReadAllText(path);
        before.ShouldContain("public record ConfirmAppointment(Guid Id);");

        // The board gives the command a field, and the code's record is still the stub the import wrote
        var writer = import(Full.Replace("      - c: Member / Confirm appointment\n", "      - c: Member / Confirm appointment\n        props: { note: string, at: datetime }\n"));

        var after = File.ReadAllText(path);
        after.ShouldContain("public record ConfirmAppointment(Guid Id, string Note, DateTimeOffset At);");
        var inserted = insertedOnly(before, after);
        inserted.ShouldBe(", string Note, DateTimeOffset At");

        var merged = writer.Callouts.Where(x => x.Code == ImportCalloutCodes.MergedField).ToList();
        merged.Select(x => x.Field).ShouldBe(["Note", "At"]);
        merged.ShouldAllBe(x => x.Type == "ConfirmAppointment" && x.File == path);
        writer.Lines.ShouldContain($"Added 2 fields (ConfirmAppointment.Note, ConfirmAppointment.At) to {Path.Combine("Features", "Booking", "ConfirmAppointment.cs")}.");
        writer.Files.ShouldContain(x => x.Path == path && x.Mode == "merge");

        buildApp();
    }

    [Fact]
    public void a_new_board_field_is_not_appended_once_the_type_has_behaviour()
    {
        import(Full);
        var path = Path.Combine(app, "Features", "Booking", "ConfirmAppointment.cs");
        var edited = File.ReadAllText(path).Replace("public record ConfirmAppointment(Guid Id);",
            "public record ConfirmAppointment(Guid Id) { public bool IsValid => Id != Guid.Empty; }");
        File.WriteAllText(path, edited);

        var writer = import(Full.Replace("      - c: Member / Confirm appointment\n", "      - c: Member / Confirm appointment\n        props: { note: string }\n"));

        File.ReadAllText(path).ShouldBe(edited);
        var differs = writer.Callouts.Single(x => x.Code == ImportCalloutCodes.FieldDiffersFromBoard);
        differs.Field.ShouldBe("Note");
        differs.Message.ShouldContain("which the code in ConfirmAppointment.cs does not declare");
        differs.Fix.ShouldBe("Add string Note to ConfirmAppointment in ConfirmAppointment.cs.");
        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.MergedField);
    }

    [Fact]
    public void a_new_board_field_is_appended_to_an_aggregate_that_is_still_a_stub()
    {
        import(Full);
        var path = Path.Combine(app, "Features", "Booking", "Appointment.cs");
        var before = File.ReadAllText(path);
        before.ShouldContain("public class Appointment { public Guid Id { get; set; } }");

        // An aggregate's fields come from an element of its name; the one-line class stays one line
        var writer = import(Full.Replace("      - e: Member / Appointment confirmed\n", "      - e: Member / Appointment confirmed\n        props: { appointmentId: uuid, when: datetime }\n"));

        writer.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.MergeSkippedUnparseable);
        buildApp();
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

        // The new role is in the fresh declaration itself; there is nothing to reopen
        var definition = read("Shelter/Features/Booking/BookingModel.cs");
        definition.ShouldContain(".Emits<AppointmentConfirmed>()");
        definition.ShouldContain(".Emits<ReminderScheduled>()");
        // bobcat#845: each emitted event's stream lane follows its Emits
        definition.ShouldContain(".Emits<AppointmentConfirmed>()\n            .OnStream<Appointment>()");
        definition.ShouldNotContain("model.Slice(");
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

    [Fact]
    public void the_roles_a_callout_is_about_are_in_the_json()
    {
        var json = ImportJson.Write("Shelter",
            [new ImportCallout("ConfirmAppointment", ImportCalloutCodes.MergedSliceRoles, [], "Added", null) { File = "/a/B.cs", Roles = ["Emits(X)", "On(Y)"] }], []);

        json.ShouldContain("\"code\": \"merged-slice-roles\"");
        json.ShouldContain("\"roles\": [\n        \"Emits(X)\",\n        \"On(Y)\"\n      ]");
        ImportJson.Write("Shelter", [new ImportCallout("", ImportCalloutCodes.MergedType, [], "Added X", null)], []).ShouldNotContain("\"roles\"");
    }
}
