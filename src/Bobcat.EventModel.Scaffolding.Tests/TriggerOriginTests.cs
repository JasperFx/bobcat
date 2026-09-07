using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// The issue #223 acceptance fixture, shaped after the K9CRUSH BookingAppointments chapter that
/// found it: three of eleven slices were automations triggered by events raised in <em>other</em>
/// chapters of the board.
/// </summary>
/// <remarks>
/// The scaffolder emitted <c>Handle(HomeCheckAssignmentAccepted trigger, …)</c> and nothing
/// anywhere declared that record, so a perfectly ordinary chapter did not compile until a human
/// worked out that the trigger was an inbound integration contract. The model already had the
/// vocabulary — <c>externalSystems:</c> with <c>direction: Inbound</c> — it just was not wired to
/// the trigger. This is the mirror of <see cref="BusVisibilityTests"/>: bus visibility asks where
/// a published message goes, this asks where a trigger event came from, and both answers come
/// from the same edge read on opposite sides.
/// </remarks>
public class TriggerOriginTests
{
    internal const string ModelYaml =
        """
        schema: 1
        model: K9Crush
        namespace: K9Crush
        slices:
          # Raised in the volunteering chapter, and the model says so.
          - name: ProposeHomeCheckAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Home check assignment accepted }
            aggregates: [Appointment]
            events: [HomeCheckAppointmentProposed]
            externalSystems:
              - { name: Volunteering, direction: Inbound }
            elements:
              HomeCheckAssignmentAccepted:
                fields: { assignmentId: Guid, volunteerId: Guid }
          # A second automation on the SAME inbound trigger: one contract, not two.
          - name: NotifyCoordinator
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Home check assignment accepted }
            aggregates: [Appointment]
            events: [CoordinatorNotified]
            externalSystems:
              - { name: Volunteering, direction: Inbound }
          # Triggered by an event this model does emit: nobody else's business.
          - name: ConfirmAppointment
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Home check appointment proposed }
            aggregates: [Appointment]
            events: [AppointmentConfirmed]
          # No emitter and no inbound edge: the modeling gap.
          - name: CancelOnWithdrawal
            pattern: Automation
            domain: Appointments
            trigger: { kind: MessageHandler, label: Adopter withdrew }
            aggregates: [Appointment]
            events: [AppointmentCancelled]
        """;

    private static CuratedModelFile model()
    {
        var reading = CuratedModelReader.Read(ModelYaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static TriggerOrigin resolve(string sliceName)
    {
        var file = model();
        return TriggerOrigins.Resolve(file, file.Slices.Single(x => x.Name == sliceName))!;
    }

    [Fact]
    public void a_trigger_another_slice_emits_belongs_to_that_slice()
    {
        var origin = resolve("ConfirmAppointment");

        origin.Event.ShouldBe("HomeCheckAppointmentProposed");
        origin.Source.ShouldBe(TriggerSource.Emitted);
        origin.EmittedBy.ShouldBe("ProposeHomeCheckAppointment");
        origin.OwnsTheContract.ShouldBeFalse();

        // ...so no second declaration of it anywhere.
        SliceScaffolder.ScaffoldTriggerContracts(model()).Keys
            .ShouldNotContain("Appointments/HomeCheckAppointmentProposed.cs");
    }

    [Fact]
    public void an_inbound_external_edge_makes_the_dangling_trigger_a_declared_contract()
    {
        var origin = resolve("ProposeHomeCheckAppointment");

        origin.Source.ShouldBe(TriggerSource.Inbound);
        origin.ExternalSystem.ShouldBe("Volunteering");
        origin.OwnsTheContract.ShouldBeTrue();

        var code = SliceScaffolder.ScaffoldTriggerContracts(model())["Appointments/HomeCheckAssignmentAccepted.cs"];

        // Fields come from the element hints, exactly like any other scaffolded record.
        code.ShouldContain("public record HomeCheckAssignmentAccepted(Guid AssignmentId, Guid VolunteerId);");
        code.ShouldContain("arrives from Volunteering");
        // The versioning note, because a boundary contract is not ours to change in place.
        code.ShouldContain("a breaking change is HomeCheckAssignmentAcceptedV2, never a changed field here");
        code.ShouldNotContain("WARNING");
    }

    [Fact]
    public void two_automations_on_one_inbound_trigger_share_one_contract()
    {
        // The #222 lesson, in its second setting: a type name is ONE artifact. Emitting the
        // contract into each declaring slice's file would be two declarations of one record,
        // which does not compile any better than none.
        var contracts = SliceScaffolder.ScaffoldTriggerContracts(model());

        contracts.Keys.ShouldContain("Appointments/HomeCheckAssignmentAccepted.cs");
        contracts.Count(x => x.Key.EndsWith("HomeCheckAssignmentAccepted.cs")).ShouldBe(1);

        SliceScaffolder.Scaffold(model(), model().Slices.Single(x => x.Name == "NotifyCoordinator"))
            .Single().Value.ShouldNotContain("public record HomeCheckAssignmentAccepted");
    }

    [Fact]
    public void a_trigger_nothing_emits_and_nothing_declares_external_is_a_scaffold_time_warning()
    {
        var origin = resolve("CancelOnWithdrawal");

        origin.Source.ShouldBe(TriggerSource.Dangling);
        origin.OwnsTheContract.ShouldBeTrue();

        // Still declared — a scaffold always compiles (issue #226) — but the model does not
        // account for it, and the mirror of #218's unhandled-published-command warning says so.
        var code = SliceScaffolder.ScaffoldTriggerContracts(model())["Appointments/AdopterWithdrew.cs"];
        code.ShouldContain("public record AdopterWithdrew(");
        code.ShouldContain("// WARNING (from the model): slice 'CancelOnWithdrawal' is triggered by 'AdopterWithdrew', but no slice in this model emits it");
        code.ShouldContain("extracted from a bigger board without its neighbours");

        TriggerOrigins.Warnings(model()).ShouldHaveSingleItem().ShouldContain("AdopterWithdrew");
    }

    [Fact]
    public void the_handler_takes_the_contract_type_the_model_named()
    {
        var file = model();
        var code = SliceScaffolder.Scaffold(file, file.Slices.Single(x => x.Name == "ProposeHomeCheckAppointment"))
            .Single().Value;

        // And it starts the appointment's stream (issue #239): the contract this model named
        // carries the volunteering flow's ids — assignmentId, volunteerId — and nothing that
        // identifies an Appointment, because the appointment does not exist until this slice
        // creates it. A [WriteModel] here failed the dispatch, not the body.
        code.ShouldContain("public static StartStream Handle(HomeCheckAssignmentAccepted trigger)");
    }

    [Fact]
    public void every_type_the_scaffold_names_is_a_type_the_scaffold_declares()
    {
        // The whole point of #223 stated as a property: walk every generated C# file, collect the
        // types it declares, and assert no handler parameter names one nobody declared. That is
        // what "the branch does not compile as checked out" looked like from an agent's side.
        var files = SliceScaffolder.ScaffoldAll(model())
            .Where(x => x.Key.EndsWith(".cs"))
            .ToDictionary(x => x.Key, x => x.Value);

        var declared = files.Values
            .SelectMany(code => code.Split('\n'))
            .Select(line => line.Trim())
            .Select(line =>
                line.StartsWith("public record ") ? line["public record ".Length..].Split('(')[0].Split(';')[0] :
                line.StartsWith("public class ") ? line["public class ".Length..].Split(' ')[0].Split(':')[0] :
                null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (path, code) in files)
        foreach (var line in code.Split('\n').Where(x => x.Contains(" Handle(")))
        {
            var parameterType = line.Split(" Handle(")[1].Split(' ')[0];
            declared.ShouldContain(parameterType,
                $"{path} takes a {parameterType} that no scaffolded file declares");
        }
    }
}
