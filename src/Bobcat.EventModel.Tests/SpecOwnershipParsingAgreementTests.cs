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
            authoring: projected
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

        // Issue #334: the all-projected repo — a defaults block and one exception.
        """
        schema: 1
        model: CritterCrush
        defaults:
          kind: integration
          authoring: projected
          scaffold: true
          owner: CritterCrush.Specs.{feature}Specs
        slices:
          - slice: ProposeHomeCheckAppointment
            kind: unit
            coveredBy: HomeChecks/Accepting an assignment books the home check as an appointment
        """,

        // A defaults block alone, with a comment and a literal owner.
        """
        schema: 1
        model: CritterCrush
        defaults:
          authoring: projected      # every slice, unless it says otherwise
          owner: CritterCrush.Specs.AllSpecs
        """,

        // Defaults followed by an entry that overrides every one of them.
        """
        schema: 1
        model: CritterCrush
        defaults:
          authoring: projected
          scaffold: false
        slices:
          - slice: A
            authoring: gherkin
          - slice: B
            scaffold: true
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

        // The defaults block both readers now have to understand (issue #334).
        generator.Defaults?.Kind.ShouldBe(real.Defaults?.Kind);
        generator.Defaults?.Authoring.ShouldBe(real.Defaults?.Authoring);
        generator.Defaults?.Owner.ShouldBe(real.Defaults?.Owner);
        (generator.Defaults is null).ShouldBe(real.Defaults is null);

        foreach (var entry in real.Slices)
        {
            generator.AuthoringFor(entry.Slice).ShouldBe(lane(real.Resolve(entry.Slice).Authoring), $"slice '{entry.Slice}'");
            generator.For(entry.Slice)!.Owner.ShouldBe(entry.Owner, $"slice '{entry.Slice}'");
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void both_readers_agree_about_a_slice_nobody_listed(string yaml)
    {
        // Gherkin when the file has no `defaults:` — "absent means Gherkin", the promise that
        // makes the manifest additive. With a defaults block the answer is the defaults', and the
        // two readers have to reach it the same way or BOBCAT025 fires on every projected test in
        // an all-projected repo (issue #334).
        var real = SpecOwnershipReader.Read(yaml).File.ShouldNotBeNull();
        var generator = Bobcat.Generators.SpecOwnershipManifest.Read(yaml);

        generator.AuthoringFor("NeverMentioned").ShouldBe(lane(real.Resolve("NeverMentioned").Authoring));
    }

    private static string lane(SpecAuthoring authoring) => authoring.ToString().ToLowerInvariant();
}
