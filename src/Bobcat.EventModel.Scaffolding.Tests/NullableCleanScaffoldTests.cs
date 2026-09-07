using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #242: the scaffold builds clean under <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c>, which is the
/// <c>dotnet new</c> default.
/// </summary>
/// <remarks>
/// Same principle as #226, one notch further along: "the scaffold compiles" has to mean cleanly, or
/// a repo with TreatWarningsAsErrors gets a red build out of it. The warning is also not one the
/// reader can act on — CS8618 asks them to initialize a property the projection or the Create
/// method is about to fill.
/// </remarks>
public class NullableCleanScaffoldTests
{
    private const string Model =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: ConfirmAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: Http, label: My appointments }
            command: ConfirmAppointment
            aggregates: [Appointment]
            events: [AppointmentConfirmed]
            elements:
              Appointment:
                fields:
                  ownerId: Guid
                  kind: string
                  status: string
                  scheduledFor: DateTimeOffset
                  awaitingAction: bool
              ConfirmAppointment:
                fields: { appointmentId: Guid }
        """;

    private static string aggregate()
    {
        var reading = CuratedModelReader.Read(Model);
        reading.Succeeded.ShouldBeTrue(string.Join("; ", reading.Problems));
        return SliceScaffolder.ScaffoldAggregates(reading.File!).Values.Single();
    }

    [Fact]
    public void a_string_property_is_initialized_rather_than_left_to_warn()
    {
        aggregate().ShouldContain("public string Kind { get; set; } = string.Empty;");
        aggregate().ShouldContain("public string Status { get; set; } = string.Empty;");
    }

    [Fact]
    public void a_value_type_property_is_left_alone()
    {
        // `= null!` on a Guid does not compile, and `= default` says nothing an auto-property does
        // not already say.
        var code = aggregate();

        code.ShouldContain("public Guid OwnerId { get; set; }");
        code.ShouldNotContain("public Guid OwnerId { get; set; } =");
        code.ShouldContain("public DateTimeOffset ScheduledFor { get; set; }");
        code.ShouldContain("public bool AwaitingAction { get; set; }");
    }

    [Fact]
    public void the_streams_own_id_is_still_the_plain_property_it_always_was()
    {
        aggregate().ShouldContain("public Guid Id { get; set; }");
    }
}
