using System.Text.RegularExpressions;
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
///
/// The rule belongs to the whole <see cref="ScaffoldFrame"/> family, not to the one frame that had
/// the defect when it was reported: <c>AggregateFrame</c> was the only frame emitting
/// auto-properties then, and <c>ViewSliceFrame</c> started emitting them the day #240 gave the read
/// model the columns its model always named — bare, so the read model came back holding exactly the
/// CS8618s this had just removed from the aggregate. The sweep at the bottom is what a per-frame
/// assertion could not be: it covers the next frame to emit a property before anyone remembers the
/// rule exists.
/// </remarks>
public partial class NullableCleanScaffoldTests
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
          - name: AppointmentsQueue
            pattern: View
            domain: Appointments
            projections: [AppointmentsQueueProjection]
            readModels: [AppointmentsQueue]
            elements:
              AppointmentsQueue:
                fields: { ownerId: Guid, status: string, kind: string }
        """;

    private static IReadOnlyDictionary<string, string> scaffold(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Succeeded.ShouldBeTrue(string.Join("; ", reading.Problems));
        return SliceScaffolder.ScaffoldAll(reading.File!);
    }

    private static string aggregate()
    {
        var reading = CuratedModelReader.Read(Model);
        reading.Succeeded.ShouldBeTrue(string.Join("; ", reading.Problems));
        return SliceScaffolder.ScaffoldAggregates(reading.File!)["Appointments/Appointment.cs"];
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

    [Fact]
    public void a_read_model_property_is_initialized_too()
    {
        // The frame that did not exist as a property emitter when this rule was written.
        var code = scaffold(Model)["Appointments/AppointmentsQueue.cs"];

        code.ShouldContain("public string Status { get; set; } = string.Empty;");
        code.ShouldContain("public string Kind { get; set; } = string.Empty;");
        code.ShouldContain("public Guid OwnerId { get; set; }");
        code.ShouldNotContain("public Guid OwnerId { get; set; } =");
    }

    /// <summary>
    /// The general form, over every fixture in the suite and every scaffolded file in each.
    /// </summary>
    [Fact]
    public void no_scaffolded_property_of_a_reference_type_is_left_uninitialized()
    {
        foreach (var yaml in new[]
                 {
                     Model, SpecAndCodeAgreementTests.ModelYaml, SliceScaffolderTests.ModelYaml,
                     BusVisibilityTests.ModelYaml, ScaffoldCompilesTests.ModelYaml,
                     TriggerOriginTests.ModelYaml, ViewSliceTests.ModelYaml,
                     ReadModelIdentityTests.ModelYaml, CreatingSliceTests.ModelYaml
                 })
        foreach (var (path, code) in scaffold(yaml).Where(x => x.Key.EndsWith(".cs")))
        foreach (var match in PropertyPattern().Matches(code).Cast<Match>())
        {
            var type = match.Groups[1].Value;
            if (type.EndsWith('?') || ValueTypes.Contains(type)) continue;

            match.Groups[3].Success.ShouldBeTrue(
                $"{path} leaves a non-nullable {type} property uninitialized — that is CS8618, and a repo "
                + $"with TreatWarningsAsErrors gets a red build out of it:{Environment.NewLine}{match.Value}");
        }
    }

    private static readonly HashSet<string> ValueTypes =
    [
        "Guid", "int", "long", "short", "byte", "bool", "decimal", "double", "float",
        "DateTimeOffset", "DateTime", "DateOnly", "TimeOnly", "TimeSpan"
    ];

    [GeneratedRegex(@"public (\S+) (\w+) \{ get; set; \}( = [^;]+;)?")]
    private static partial Regex PropertyPattern();
}
