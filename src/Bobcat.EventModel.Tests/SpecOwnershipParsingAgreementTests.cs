using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// The two readers of the spec-ownership manifest, pinned together (issue #324 part 4).
/// </summary>
/// <remarks>
/// <para>
/// <c>SpecOwnershipReader</c> is YamlDotNet and runs in the tool; <c>SpecOwnershipManifest</c> is a
/// hand-rolled reader inside the netstandard2.0 analyzer, which references nothing. Two readers is
/// a deliberate cost, taken for the same reason <c>GeneratorSliceTags</c> and
/// <c>CodeFirstNaming</c> are duplicated, and paid for the same way: a corpus both must agree on.
/// </para>
/// <para>
/// A divergence would be silent and expensive. The scaffolder would put a slice in one lane and
/// the build's duplicate-identity guard would judge it in another — so a slice could be scaffolded
/// as a <c>.feature</c> while the analyzer believed it projected, which is precisely the collision
/// this whole part exists to prevent.
/// </para>
/// </remarks>
public class SpecOwnershipParsingAgreementTests
{
    /// <summary>
    /// Every shape worth agreeing on: the documented example, the terse defaults, the hyphenated
    /// spelling, quoting, comments, and an entry whose first key is not <c>slice:</c>.
    /// </summary>
    public static TheoryData<string> Corpus() =>
    [
        """
        schema: 1
        model: CritterCrush
        slices:
          - slice: ProposeHomeCheckAppointment
            kind: unit
            authoring: projected
            owner: CritterCrush.Specs.ProposalSpecs
            coveredBy: HomeChecks/Accepting an assignment books the home check as an appointment
        """,

        // Terse: kind alone, authoring alone, and neither.
        """
        schema: 1
        model: CritterCrush
        slices:
          - slice: A
            kind: unit
          - slice: B
            authoring: code-first
          - slice: C
        """,

        // Quoted values and a trailing comment.
        """
        schema: 1
        model: "CritterCrush"       # the merge key
        slices:
          - slice: 'ProposeHomeCheckAppointment'
            authoring: "projected"
            owner: 'CritterCrush.Specs.ProposalSpecs'
        """,

        // The key order a hand-edited file drifts into, and a blank line between entries.
        """
        schema: 1
        model: CritterCrush
        slices:
          - kind: unit
            slice: A
            coveredBy: F/S

          - owner: Specs.B
            slice: B
            authoring: projected
        """,

        // Nothing but the header.
        """
        schema: 1
        model: CritterCrush
        """,
    ];

    [Theory]
    [MemberData(nameof(Corpus))]
    public void both_readers_see_the_same_model_and_the_same_lanes(string yaml)
    {
        var real = SpecOwnershipReader.Read(yaml).File.ShouldNotBeNull();
        var generator = Bobcat.Generators.SpecOwnershipManifest.Read(yaml);

        generator.Model.ShouldBe(real.Model);

        generator.Slices.Select(x => x.Slice)
            .ShouldBe(real.Slices.Select(x => x.Slice), ignoreOrder: false);

        foreach (var entry in real.Slices)
        {
            generator.AuthoringFor(entry.Slice).ShouldBe(lane(entry.ResolvedAuthoring), $"slice '{entry.Slice}'");
            generator.For(entry.Slice)!.Owner.ShouldBe(entry.Owner, $"slice '{entry.Slice}'");
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void both_readers_call_an_unlisted_slice_gherkin(string yaml)
    {
        var real = SpecOwnershipReader.Read(yaml).File.ShouldNotBeNull();
        var generator = Bobcat.Generators.SpecOwnershipManifest.Read(yaml);

        SpecOwnershipPlanAuthoring(real, "NeverMentioned").ShouldBe("gherkin");
        generator.AuthoringFor("NeverMentioned").ShouldBe("gherkin");
    }

    private static string SpecOwnershipPlanAuthoring(SpecOwnershipFile file, string slice)
    {
        var entry = file.Slices.FirstOrDefault(x => x.Slice == slice);
        return entry is null ? "gherkin" : lane(entry.ResolvedAuthoring);
    }

    private static string lane(SpecAuthoring authoring) => authoring.ToString().ToLowerInvariant();
}
