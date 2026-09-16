using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #324 part 4: the spec-ownership join checked against the compilation. The manifest is the
/// forward declaration and a slice tag is the backward binding; only the build sees both.
/// </summary>
public class SpecOwnershipDiagnosticTests
{
    private const string Manifest = "CritterCrush.spec-ownership.yaml";

    private const string ProjectedSource =
        """
        using System;
        using Xunit;

        [BobcatFeature("Proposals")]
        [BobcatSlice(SliceName = "ProposeHomeCheckAppointment")]
        public class proposal_specs
        {
            [Fact]
            public void an_accepted_assignment_proposes_a_visit()
            {
                // Given an accepted assignment
                // Then a visit is proposed
            }
        }
        """;

    [Fact]
    public void a_projected_test_matching_the_manifest_is_clean()
    {
        var outcome = GeneratorHarness.Run(ProjectedSource, (Manifest,
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: ProposeHomeCheckAppointment
                kind: unit
                authoring: projected
                owner: proposal_specs
                coveredBy: HomeChecks/an assignment is accepted
            """));

        outcome.WithId("BOBCAT025").ShouldBeEmpty();
        outcome.WithId("BOBCAT026").ShouldBeEmpty();
    }

    [Fact]
    public void a_projected_test_the_manifest_does_not_list_is_the_duplicate_identity_collision()
    {
        // Unlisted means gherkin, so the scaffolder will also write a .feature for this slice and
        // two specs will claim one identity. That is the whole reason the backward direction is
        // checked at all.
        var outcome = GeneratorHarness.Run(ProjectedSource, (Manifest,
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: SomethingElse
                kind: unit
                authoring: projected
                owner: other_specs
                coveredBy: F/S
            """));

        var error = outcome.WithId("BOBCAT025").ShouldHaveSingleItem();
        error.GetMessage().ShouldContain("ProposeHomeCheckAppointment");
        error.GetMessage().ShouldContain("projected");
        error.GetMessage().ShouldContain("the manifest does not list it");
    }

    [Fact]
    public void a_stale_feature_left_behind_after_switching_a_slice_to_projected_is_caught()
    {
        // The realistic route into the collision: switch the slice, forget to delete last time's
        // scaffolded .feature. Nothing else in the pipeline says a word about it.
        var outcome = GeneratorHarness.Run(ProjectedSource,
            (Manifest,
                """
                schema: 1
                model: CritterCrush
                slices:
                  - slice: ProposeHomeCheckAppointment
                    kind: unit
                    authoring: projected
                    owner: proposal_specs
                    coveredBy: F/S
                """),
            ("Proposals.feature",
                """
                Feature: Proposals

                  @slice:ProposeHomeCheckAppointment
                  Scenario: an accepted assignment proposes a visit
                    Given an accepted assignment
                """));

        var error = outcome.WithId("BOBCAT025").ShouldHaveSingleItem();
        error.GetMessage().ShouldContain("Proposals.feature");
        error.GetMessage().ShouldContain("as gherkin");
        error.GetMessage().ShouldContain("manifest says projected");
    }

    [Fact]
    public void a_manifest_entry_nothing_binds_is_a_warning_not_an_error()
    {
        // A warning because the owner may legitimately live in a sibling assembly; an error would
        // make a model-and-manifest pair shared across spec projects unbuildable.
        var outcome = GeneratorHarness.Run(ProjectedSource, (Manifest,
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: ProposeHomeCheckAppointment
                kind: unit
                authoring: projected
                owner: proposal_specs
                coveredBy: F/S
              - slice: RescheduleAppointment
                kind: unit
                authoring: projected
                owner: reschedule_specs
                coveredBy: F/S
            """));

        var warning = outcome.WithId("BOBCAT026").ShouldHaveSingleItem();
        warning.Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
        warning.GetMessage().ShouldContain("RescheduleAppointment");
        warning.GetMessage().ShouldContain("reschedule_specs");
    }

    [Fact]
    public void a_gherkin_slice_the_manifest_never_mentions_is_untouched()
    {
        var outcome = GeneratorHarness.Run("",
            (Manifest,
                """
                schema: 1
                model: CritterCrush
                slices:
                  - slice: ProposeHomeCheckAppointment
                    kind: unit
                    authoring: projected
                    owner: proposal_specs
                    coveredBy: F/S
                """),
            ("Booking.feature",
                """
                Feature: Booking

                  @slice:ConfirmAppointment
                  Scenario: a proposal is confirmed
                    Given a proposed appointment
                """));

        outcome.WithId("BOBCAT025").ShouldBeEmpty();
    }

    [Fact]
    public void without_a_manifest_nothing_is_reported_at_all()
    {
        // Adopting the manifest is the opt-in. Everything that builds today keeps building, and
        // the projected lane that shipped in part 1 gains no new obligation.
        var outcome = GeneratorHarness.Run(ProjectedSource);

        outcome.WithId("BOBCAT025").ShouldBeEmpty();
        outcome.WithId("BOBCAT026").ShouldBeEmpty();
    }

    [Fact]
    public void a_yaml_file_that_is_not_named_as_a_manifest_is_not_read_as_one()
    {
        var outcome = GeneratorHarness.Run(ProjectedSource, ("notes.yaml",
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: SomethingElse
                kind: unit
            """));

        outcome.WithId("BOBCAT025").ShouldBeEmpty();
    }
}

/// <summary>
/// The generator's manifest reader is a second implementation of a format YamlDotNet already reads,
/// duplicated for the same netstandard2.0 reason as <c>GeneratorSliceTags</c> and
/// <c>CodeFirstNaming</c>. These pin the shapes it must get right; the agreement test in
/// <c>Bobcat.EventModel.Tests</c> pins it against the real reader.
/// </summary>
public class GeneratorManifestReaderTests
{
    [Fact]
    public void it_reads_the_documented_manifest()
    {
        var manifest = SpecOwnershipManifest.Read(
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: ProposeHomeCheckAppointment
                kind: unit
                authoring: projected
                owner: CritterCrush.Specs.ProposalSpecs
                coveredBy: HomeChecks/Accepting an assignment books the home check as an appointment
            """);

        manifest.Model.ShouldBe("CritterCrush");
        var entry = manifest.Slices.ShouldHaveSingleItem();
        entry.Slice.ShouldBe("ProposeHomeCheckAppointment");
        entry.Owner.ShouldBe("CritterCrush.Specs.ProposalSpecs");
        entry.ResolvedAuthoring.ShouldBe("projected");
    }

    [Fact]
    public void an_unlisted_slice_is_gherkin()
        => SpecOwnershipManifest.Read("schema: 1").AuthoringFor("Anything").ShouldBe("gherkin");

    [Fact]
    public void a_terse_unit_entry_resolves_to_projected()
    {
        SpecOwnershipManifest.Read(
            """
            slices:
              - slice: X
                kind: unit
            """).AuthoringFor("X").ShouldBe("projected");
    }

    [Fact]
    public void a_hash_inside_a_value_is_not_a_comment()
    {
        // A naive split on '#' truncates `coveredBy: Sprint #3/…`, which is the kind of value this
        // file really carries.
        SpecOwnershipManifest.Read(
            """
            slices:
              - slice: X   # this IS a comment
                owner: Team#3.Specs
            """).For("X")!.Owner.ShouldBe("Team#3.Specs");
    }

    [Fact]
    public void the_file_name_convention_is_what_selects_a_manifest()
    {
        SpecOwnershipManifest.IsManifestFile("/repo/CritterCrush.spec-ownership.yaml").ShouldBeTrue();
        SpecOwnershipManifest.IsManifestFile("/repo/specownership.yml").ShouldBeTrue();
        SpecOwnershipManifest.IsManifestFile("/repo/CritterCrush.emodel.yaml").ShouldBeFalse();
    }
}
