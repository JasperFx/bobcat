using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#383: a declared type's full name is where the code puts it, so one aggregate is one
/// swimlane on the canvas rather than two. The mapper used to synthesize <c>{namespace}.{Name}</c>
/// while the stubs went into a namespace per chapter, and the two names never met.
/// </summary>
/// <remarks>
/// The model is CritterCrush's shape in miniature, and the Scheduling slices come <em>first</em> on
/// purpose: an event a Scheduling slice consumes but Volunteering emits must still be placed by the
/// slice that emits it, which "the first slice that names it" would get wrong.
/// </remarks>
public class DeclaredFullNameTests
{
    private const string CritterCrush =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: ProposeHomeCheckAppointment
            pattern: Automation
            chapter: Scheduling
            command: ProposeHomeCheckAppointment
            consumedEvents: [HomeCheckAssignmentAccepted]
            startsStream: Appointment
            events: [HomeCheckAppointmentProposed]
          - name: ScheduleFosterIntake
            pattern: Automation
            chapter: Scheduling
            command: ScheduleFosterIntake
            consumedEvents: [DogPlacedInFoster]
            startsStream: Appointment
            events: [FosterIntakeProposed]
          - name: AppointmentsQueue
            pattern: View
            chapter: Scheduling
            readModels: [AppointmentsQueue]
            consumedEvents: [HomeCheckAppointmentProposed, FosterIntakeProposed]
          - name: AcceptHomeCheckAssignment
            pattern: Command
            chapter: Volunteering
            command: AcceptHomeCheckAssignment
            handler: AcceptHomeCheckAssignmentHandler
            aggregates: [HomeCheck]
            events: [HomeCheckAssignmentAccepted]
        """;

    private static ImportedEventModel model()
    {
        var reading = CuratedModelReader.Read(CritterCrush);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static EventModelSliceDescriptor slice(EventModelDescriptor descriptor, string name)
        => descriptor.Slices.Single(x => x.Name == name);

    [Fact]
    public void an_event_lives_in_the_chapter_of_the_slice_that_emits_it()
    {
        var descriptor = ImportedModelMapper.ToDescriptor(model());

        // Consumed in Scheduling, emitted in Volunteering: it is Volunteering's type
        slice(descriptor, "ProposeHomeCheckAppointment").ConsumedEvents.Single().FullName
            .ShouldBe("CritterCrush.Volunteering.HomeCheckAssignmentAccepted");
        slice(descriptor, "AcceptHomeCheckAssignment").EmittedEvents.Single().FullName
            .ShouldBe("CritterCrush.Volunteering.HomeCheckAssignmentAccepted");
    }

    [Fact]
    public void an_inbound_event_nothing_emits_lives_in_the_chapter_that_consumes_it()
    {
        slice(ImportedModelMapper.ToDescriptor(model()), "ScheduleFosterIntake").ConsumedEvents.Single().FullName
            .ShouldBe("CritterCrush.Scheduling.DogPlacedInFoster");
    }

    [Fact]
    public void an_aggregate_is_one_type_whichever_slice_names_it()
    {
        // The issue's symptom: CritterCrush.Appointment, a type that does not exist, beside the real
        // CritterCrush.Scheduling.Appointment, drew two swimlanes labelled the same
        var descriptor = ImportedModelMapper.ToDescriptor(model());

        descriptor.Slices
            .SelectMany(x => x.AggregateTypes.Concat(x.StartsStream is null ? [] : [x.StartsStream]))
            .Where(x => x.Name == "Appointment")
            .Select(x => x.FullName)
            .Distinct()
            .ShouldBe(["CritterCrush.Scheduling.Appointment"]);
    }

    [Fact]
    public void a_handler_shares_its_commands_namespace_because_the_scaffold_appends_it_there()
    {
        slice(ImportedModelMapper.ToDescriptor(model()), "AcceptHomeCheckAssignment").HandlerType!.FullName
            .ShouldBe("CritterCrush.Volunteering.AcceptHomeCheckAssignmentHandler");
    }

    [Fact]
    public void every_declared_full_name_is_the_full_name_the_generated_code_has()
    {
        // The agreement test: compile the stubs and the definition the import writes, and compare the
        // definition's types (real CLR types, through the generic role calls) with the mapper's. A
        // handler is declared by name in the code, so it carries no namespace there and is left out.
        var file = model();
        var generated = CSharpModelWriter.Write(file);

        var fromCode = fullNames(CSharpModelWriterTests.build(generated));
        var fromModel = fullNames(ImportedModelMapper.ToDescriptor(file, generated.Layout));

        fromModel.ShouldBe(fromCode);
        fromCode.ShouldContain("HomeCheckAssignmentAccepted=CritterCrush.Volunteering.HomeCheckAssignmentAccepted");
    }

    [Fact]
    public void the_default_layout_is_the_one_the_writer_uses()
    {
        var file = model();

        fullNames(ImportedModelMapper.ToDescriptor(file))
            .ShouldBe(fullNames(ImportedModelMapper.ToDescriptor(file, CSharpModelWriter.Write(file).Layout)));
    }

    /// <summary>
    /// Every type a slice names, as <c>Name=FullName</c>, once each. Deliberately a set of types and not
    /// of roles: which roles a type fills is the round-trip test's business in
    /// <see cref="CSharpModelWriterTests"/>, and the builder lists a started stream among a slice's
    /// aggregates where the mapper does not, which says nothing about where the type lives.
    /// </summary>
    private static IReadOnlyList<string> fullNames(EventModelDescriptor descriptor)
        => descriptor.Slices
            .SelectMany(s => new[] { s.CommandType, s.StartsStream }
                .Concat(s.AggregateTypes)
                .Concat(s.EmittedEvents)
                .Concat(s.PublishedMessages)
                .Concat(s.ConsumedEvents)
                .Concat(s.ReadsFrom)
                .Concat(s.ReadModelTypes))
            .OfType<TypeDescriptor>()
            .Select(t => $"{t.Name}={t.FullName}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
}
