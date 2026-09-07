using System.Text.RegularExpressions;
using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #242: a scaffold is warning-clean under <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c>,
/// which is what <c>dotnet new</c> gives every project.
/// </summary>
/// <remarks>
/// A non-nullable reference-typed auto-property with no initializer is CS8618 — two warnings out
/// of one seven-field aggregate — and a repo with <c>TreatWarningsAsErrors</c> gets a red build
/// out of a scaffold #226 established should be green. Same principle as #226, one notch quieter:
/// the scaffold's job is to hand back something that builds, so the only thing left to do is the
/// decision.
/// </remarks>
public partial class NullableCleanScaffoldTests
{
    internal const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: ProposeAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: MessageHandler }
            command: ProposeAppointment
            aggregates: [Appointment]
            events: [AppointmentProposed]
            elements:
              Appointment:
                fields: { ownerId: Guid, kind: string, status: string, scheduledFor: DateTimeOffset,
                          awaitingAction: bool }
          - name: AppointmentsQueue
            pattern: View
            domain: Appointments
            projections: [AppointmentsQueueProjection]
            readModels: [AppointmentsQueue]
            elements:
              AppointmentsQueue:
                fields: { ownerId: Guid, status: string }
        """;

    private static IReadOnlyDictionary<string, string> scaffold(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return SliceScaffolder.ScaffoldAll(reading.File!);
    }

    [Fact]
    public void a_scaffolded_aggregate_initializes_its_reference_typed_properties()
    {
        var code = scaffold(ModelYaml)["Appointments/Appointment.cs"];

        code.ShouldContain("public string Kind { get; set; } = string.Empty;");
        code.ShouldContain("public string Status { get; set; } = string.Empty;");

        // Value types were never the problem, and adding an initializer to one would be noise.
        code.ShouldContain("public Guid OwnerId { get; set; }");
        code.ShouldContain("public DateTimeOffset ScheduledFor { get; set; }");
        code.ShouldContain("public bool AwaitingAction { get; set; }");
    }

    [Fact]
    public void a_scaffolded_read_model_does_too()
        => scaffold(ModelYaml)["Appointments/AppointmentsQueue.cs"]
            .ShouldContain("public string Status { get; set; } = string.Empty;");

    /// <summary>
    /// The general form, run over every fixture in the suite: the invariant belongs to the
    /// <see cref="ScaffoldFrame"/> family, so a frame that emits a bare reference-typed property
    /// must trip here whichever model happens to exercise it.
    /// </summary>
    [Fact]
    public void no_scaffolded_property_of_a_reference_type_is_left_uninitialized()
    {
        foreach (var yaml in new[]
                 {
                     ModelYaml, SpecAndCodeAgreementTests.ModelYaml, SliceScaffolderTests.ModelYaml,
                     BusVisibilityTests.ModelYaml, ScaffoldCompilesTests.ModelYaml,
                     TriggerOriginTests.ModelYaml, ViewSliceTests.ModelYaml
                 })
        foreach (var (path, code) in scaffold(yaml).Where(x => x.Key.EndsWith(".cs")))
        foreach (var match in PropertyPattern().Matches(code).Cast<Match>())
        {
            var type = match.Groups[1].Value;
            if (type is "Guid" or "int" or "long" or "bool" or "decimal" or "double"
                or "DateTimeOffset" or "DateOnly" or "TimeSpan" || type.EndsWith('?')) continue;

            match.Groups[3].Success.ShouldBeTrue(
                $"{path} leaves a non-nullable {type} property uninitialized — that is CS8618, and a repo "
                + $"with TreatWarningsAsErrors gets a red build out of it:{Environment.NewLine}{match.Value}");
        }
    }

    [GeneratedRegex(@"public (\S+) (\w+) \{ get; set; \}( = [^;]+;)?")]
    private static partial Regex PropertyPattern();
}
