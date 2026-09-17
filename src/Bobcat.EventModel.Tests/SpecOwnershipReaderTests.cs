using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// Issue #324 part 4: the spec-ownership manifest — which slices are specified somewhere other
/// than the <c>.feature</c> the scaffolder would write, and the validation that keeps a file
/// nothing can derive from rotting silently.
/// </summary>
public class SpecOwnershipReaderTests
{
    private const string Manifest =
        """
        schema: 1
        model: CritterCrush
        slices:
          - slice: ProposeHomeCheckAppointment
            kind: unit
            authoring: projected
            owner: CritterCrush.Specs.ProposalSpecs
            coveredBy: HomeChecks/Accepting an assignment books the home check as an appointment
        """;

    private static CuratedModelFile model() => new()
    {
        Schema = 1,
        Model = "CritterCrush",
        Slices =
        [
            new CuratedSlice { Name = "ProposeHomeCheckAppointment" },
            new CuratedSlice
            {
                Name = "AcceptHomeCheckAssignment",
                Specifications = new CuratedSpecifications
                {
                    Feature = "HomeChecks",
                    Scenarios =
                    [
                        new CuratedScenario { Name = "Accepting an assignment books the home check as an appointment" }
                    ]
                }
            }
        ]
    };

    [Fact]
    public void the_documented_manifest_reads_and_validates()
    {
        var reading = SpecOwnershipReader.Read(Manifest, model());

        reading.Problems.ShouldBeEmpty();
        reading.Succeeded.ShouldBeTrue();

        var entry = reading.File!.Slices.ShouldHaveSingleItem();
        entry.Slice.ShouldBe("ProposeHomeCheckAppointment");
        entry.Owner.ShouldBe("CritterCrush.Specs.ProposalSpecs");

        var resolved = reading.File!.Resolve("ProposeHomeCheckAppointment");
        resolved.Kind.ShouldBe(SpecKind.Unit);
        resolved.Authoring.ShouldBe(SpecAuthoring.Projected);
        resolved.SuppressesFeature.ShouldBeTrue();
        resolved.Scaffold.ShouldBeTrue();
    }

    [Fact]
    public void an_unlisted_slice_is_untouched_which_is_what_makes_the_manifest_additive()
    {
        // The whole compatibility story: CritterCrush needs three entries, not nineteen, and
        // adopting the file cannot change what an existing repo scaffolds for anything else.
        var reading = SpecOwnershipReader.Read(Manifest, model());

        reading.File!.Slices.ShouldNotContain(x => x.Slice == "AcceptHomeCheckAssignment");
    }

    [Fact]
    public void kind_and_authoring_are_orthogonal_so_integration_may_be_projected()
    {
        // Marten's DaemonTests is the shipped counter-example to collapsing these into one axis:
        // real database tests, rendered through marker steps.
        var reading = SpecOwnershipReader.Read(
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: ProposeHomeCheckAppointment
                kind: integration
                authoring: projected
                scaffold: true
                owner: CritterCrush.Specs.ProposalSpecs
            """,
            model());

        reading.Problems.ShouldBeEmpty();

        var resolved = reading.File!.Resolve(reading.File!.Slices.ShouldHaveSingleItem().Slice);
        resolved.Kind.ShouldBe(SpecKind.Integration);
        resolved.Authoring.ShouldBe(SpecAuthoring.Projected);

        // And no coveredBy is demanded: an integration spec IS the end-to-end cover.
        resolved.CoveredBy.ShouldBeNull();

        // Issue #334: and it is scaffolded, which the row this pairing came from never was.
        resolved.Scaffold.ShouldBeTrue();
    }

    [Fact]
    public void unit_with_a_store_backed_authoring_is_the_one_impossible_corner()
    {
        var problems = SpecOwnershipReader.Validate(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices = [new SpecOwnership { Slice = "X", Kind = "unit", Authoring = "gherkin", CoveredBy = "F/S" }]
        });

        problems.ShouldContain(x => x.Contains("kind 'unit' cannot be authored as 'gherkin'"));
    }

    [Fact]
    public void a_terse_unit_entry_resolves_to_projected_rather_than_to_the_invalid_corner()
    {
        // `unit` names the only pairing the format permits, so defaulting authoring to gherkin
        // would make every entry that says nothing invalid for saying nothing.
        var file = new SpecOwnershipFile
        {
            Schema = 1, Model = "M", Slices = [new SpecOwnership { Slice = "X", Kind = "unit" }]
        };

        file.Resolve("X").Authoring.ShouldBe(SpecAuthoring.Projected);
    }

    [Fact]
    public void code_first_spells_with_a_hyphen_and_the_enum_does_not()
    {
        new SpecOwnershipFile
            {
                Schema = 1, Model = "M", Slices = [new SpecOwnership { Slice = "X", Authoring = "code-first" }]
            }
            .Resolve("X").Authoring.ShouldBe(SpecAuthoring.CodeFirst);
    }

    [Fact]
    public void a_unit_slice_must_name_the_scenario_that_covers_it_end_to_end()
    {
        var problems = SpecOwnershipReader.Validate(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices = [new SpecOwnership { Slice = "X", Kind = "unit", Authoring = "projected" }]
        });

        problems.ShouldContain(x => x.Contains("`coveredBy:` is required"));
    }

    [Fact]
    public void a_coveredBy_naming_no_declared_scenario_is_the_rot_this_file_is_exposed_to()
    {
        // CritterCrush's hand-written Stoat plan carried eleven spec identities matching no
        // scenario, silently. A manifest cannot be derived, so validation is the only defence.
        var reading = SpecOwnershipReader.Read(
            Manifest.Replace("HomeChecks/Accepting", "HomeChecks/Renamed"), model());

        reading.Problems.ShouldContain(x => x.Contains("coveredBy 'HomeChecks/Renamed"));
        reading.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void the_feature_half_of_an_identity_defaults_to_the_slice_name()
    {
        var identities = SpecOwnershipReader.DeclaredIdentities(new CuratedModelFile
        {
            Slices =
            [
                new CuratedSlice
                {
                    Name = "ConfirmAppointment",
                    Specifications = new CuratedSpecifications
                    {
                        Scenarios = [new CuratedScenario { Name = "a proposal is confirmed" }]
                    }
                }
            ]
        });

        identities.ShouldContain("ConfirmAppointment/a proposal is confirmed");
    }

    [Fact]
    public void a_slice_the_model_does_not_declare_is_a_problem()
    {
        var problems = SpecOwnershipReader.Validate(
            new SpecOwnershipFile { Schema = 1, Model = "CritterCrush", Slices = [new SpecOwnership { Slice = "Ghost" }] },
            model());

        problems.ShouldContain(x => x.Contains("slice 'Ghost' is not in the event model"));
    }

    [Fact]
    public void a_model_name_that_does_not_match_means_the_two_files_never_join()
    {
        var problems = SpecOwnershipReader.Validate(
            new SpecOwnershipFile { Schema = 1, Model = "SomethingElse" }, model());

        problems.ShouldContain(x => x.Contains("`model: SomethingElse`"));
    }

    [Fact]
    public void a_slice_listed_twice_is_a_problem_because_one_slice_is_specified_in_one_place()
    {
        var problems = SpecOwnershipReader.Validate(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices = [new SpecOwnership { Slice = "X" }, new SpecOwnership { Slice = "X" }]
        });

        problems.ShouldContain(x => x.Contains("slice 'X' is listed more than once"));
    }

    [Fact]
    public void a_missing_schema_reads_as_zero_and_says_so()
    {
        var reading = SpecOwnershipReader.Read("model: CritterCrush");

        reading.Problems.ShouldContain(x => x.Contains("schema must be 1"));
    }

    [Fact]
    public void an_unknown_kind_is_named_rather_than_silently_defaulted()
    {
        var problems = SpecOwnershipReader.Validate(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices = [new SpecOwnership { Slice = "X", Kind = "smoke" }]
        });

        problems.ShouldContain(x => x.Contains("kind 'smoke' is not one of"));
    }

    [Fact]
    public void one_owner_cannot_be_two_authoring_styles()
    {
        var problems = SpecOwnershipReader.Validate(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices =
            [
                new SpecOwnership { Slice = "A", Authoring = "projected", Owner = "Specs.Shared" },
                new SpecOwnership { Slice = "B", Authoring = "code-first", Owner = "Specs.Shared" }
            ]
        });

        problems.ShouldContain(x => x.Contains("owner 'Specs.Shared' is named by slices authored"));
    }

    [Fact]
    public void one_owner_cannot_cover_two_features_because_the_feature_is_class_level()
    {
        var twoFeatures = new CuratedModelFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices =
            [
                new CuratedSlice { Name = "A", Specifications = new CuratedSpecifications { Feature = "One" } },
                new CuratedSlice { Name = "B", Specifications = new CuratedSpecifications { Feature = "Two" } }
            ]
        };

        var problems = SpecOwnershipReader.Validate(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices =
            [
                new SpecOwnership { Slice = "A", Authoring = "projected", Owner = "Specs.Shared" },
                new SpecOwnership { Slice = "B", Authoring = "projected", Owner = "Specs.Shared" }
            ]
        }, twoFeatures);

        problems.ShouldContain(x => x.Contains("covers slices in more than one feature"));
    }

    [Fact]
    public void a_scenario_name_a_method_name_cannot_spell_is_warned_about()
    {
        // A projected test's identity IS its method name, so punctuation silently publishes a
        // different identity — the same shape of silent degradation as issue #318.
        var punctuated = new CuratedModelFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices =
            [
                new CuratedSlice
                {
                    Name = "ProposeHomeCheckAppointment",
                    Specifications = new CuratedSpecifications
                    {
                        Scenarios = [new CuratedScenario { Name = "an assignment, once accepted, books a visit" }]
                    }
                }
            ]
        };

        var warnings = SpecOwnershipReader.Warn(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices =
            [
                new SpecOwnership
                {
                    Slice = "ProposeHomeCheckAppointment", Kind = "unit", Authoring = "projected",
                    Owner = "Specs.ProposalSpecs", CoveredBy = "x/y"
                }
            ]
        }, punctuated);

        warnings.ShouldContain(x =>
            x.Contains("will publish 'an assignment once accepted books a visit' instead and join nothing"));
    }

    [Fact]
    public void a_projected_slice_with_no_owner_is_warned_about_but_still_loads()
    {
        var reading = SpecOwnershipReader.Read(
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: ProposeHomeCheckAppointment
                authoring: projected
                scaffold: true
            """,
            model());

        reading.Succeeded.ShouldBeTrue();
        reading.Warnings.ShouldContain(x => x.Contains("names no `owner:`"));
    }

    [Fact]
    public void scenario_bodies_left_in_the_model_for_an_adopted_slice_are_warned_about()
    {
        // `scaffold: false` — an existing suite covers the slice, so the model's scenario bodies
        // are dead weight nothing will read.
        var withBodies = model();
        withBodies.Slices[0].Specifications = new CuratedSpecifications
        {
            Scenarios = [new CuratedScenario { Name = "a proposal is made" }]
        };

        var warnings = SpecOwnershipReader.Warn(
            SpecOwnershipReader.Read(
                """
                schema: 1
                model: CritterCrush
                slices:
                  - slice: ProposeHomeCheckAppointment
                    authoring: projected
                    scaffold: false
                    owner: Existing.Suite.ProposalTests
                """).File!,
            withBodies);

        warnings.ShouldContain(x => x.Contains("will not be scaffolded"));
    }

    // --- Issue #334 ---

    [Fact]
    public void a_projected_integration_slice_must_say_whether_it_is_scaffolded()
    {
        // The row conflated "an existing suite covers this" with "generate me one", and nothing
        // the scaffolder can see tells them apart — so the format asks rather than guessing.
        var reading = SpecOwnershipReader.Read(
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: ProposeHomeCheckAppointment
                kind: integration
                authoring: projected
                owner: CritterCrush.Specs.ProposalSpecs
            """,
            model());

        reading.Problems.ShouldContain(x => x.Contains("must say `scaffold:`"));
    }

    [Fact]
    public void every_other_combination_answers_itself()
    {
        // The requirement is narrow on purpose: it applies to the one corner where both readings
        // are plausible. A unit slice is always scaffolded, and so is a code-first one.
        var file = SpecOwnershipReader.Read(
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: A
                kind: unit
                coveredBy: F/S
              - slice: B
                authoring: code-first
              - slice: C
            """).File!;

        file.Resolve("A").Scaffold.ShouldBeTrue();
        file.Resolve("B").Scaffold.ShouldBeTrue();
        file.Resolve("C").Scaffold.ShouldBeTrue();
        SpecOwnershipReader.Validate(file).ShouldNotContain(x => x.Contains("must say `scaffold:`"));
    }

    [Fact]
    public void a_scaffolded_projected_slice_is_not_warned_about_for_having_scenario_bodies()
    {
        // The second finding on #334: an all-projected repo produced 19 near-identical warnings
        // saying the bodies would not be scaffolded. With `scaffold: true` they ARE — as method
        // names and step comments — so the warning dissolves rather than needing a cap.
        var withBodies = model();
        withBodies.Slices[0].Specifications = new CuratedSpecifications
        {
            Scenarios = [new CuratedScenario { Name = "a proposal is made" }]
        };

        var warnings = SpecOwnershipReader.Warn(
            SpecOwnershipReader.Read(
                """
                schema: 1
                model: CritterCrush
                defaults:
                  authoring: projected
                  scaffold: true
                  owner: CritterCrush.Specs.{feature}Specs
                """).File!,
            withBodies);

        warnings.ShouldNotContain(x => x.Contains("will not be scaffolded"));
        warnings.ShouldNotContain(x => x.Contains("names no `owner:`"));
    }

    [Fact]
    public void defaults_decide_a_slice_the_file_never_lists()
    {
        var file = SpecOwnershipReader.Read(
            """
            schema: 1
            model: CritterCrush
            defaults:
              kind: integration
              authoring: projected
              scaffold: true
              owner: CritterCrush.Specs.{feature}Specs
            """).File!;

        var resolved = file.Resolve("ConfirmAppointment", "BookingAppointments");
        resolved.Authoring.ShouldBe(SpecAuthoring.Projected);
        resolved.Kind.ShouldBe(SpecKind.Integration);
        resolved.Scaffold.ShouldBeTrue();
        resolved.Owner.ShouldBe("CritterCrush.Specs.BookingAppointmentsSpecs");
        resolved.Listed.ShouldBeFalse();
    }

    [Fact]
    public void an_entry_overrides_every_default_it_states()
    {
        var file = SpecOwnershipReader.Read(
            """
            schema: 1
            model: CritterCrush
            defaults:
              authoring: projected
              scaffold: true
              owner: CritterCrush.Specs.{feature}Specs
            slices:
              - slice: A
                authoring: gherkin
              - slice: B
                scaffold: false
              - slice: C
                owner: Existing.Suite.Tests
            """).File!;

        file.Resolve("A").Authoring.ShouldBe(SpecAuthoring.Gherkin);
        file.Resolve("B").Scaffold.ShouldBeFalse();
        file.Resolve("C").Owner.ShouldBe("Existing.Suite.Tests");

        // And what it does not state still comes from the defaults.
        file.Resolve("C").Authoring.ShouldBe(SpecAuthoring.Projected);
    }

    [Fact]
    public void an_entrys_own_unit_kind_beats_a_gherkin_default()
    {
        // Unit is the one kind the other styles cannot express, so `kind: unit` implies projected
        // whatever the defaults say — otherwise a terse unit entry would be invalid for saying
        // nothing, which is the trap the pre-#334 default already avoided per entry.
        var file = SpecOwnershipReader.Read(
            """
            schema: 1
            model: CritterCrush
            defaults:
              authoring: gherkin
            slices:
              - slice: A
                kind: unit
                coveredBy: F/S
            """).File!;

        file.Resolve("A").Authoring.ShouldBe(SpecAuthoring.Projected);
        SpecOwnershipReader.Validate(file).ShouldBeEmpty();
    }

    [Fact]
    public void defaults_that_land_in_the_ambiguous_corner_must_answer_for_every_unlisted_slice()
    {
        SpecOwnershipReader.Validate(SpecOwnershipReader.Read(
                """
                schema: 1
                model: CritterCrush
                defaults:
                  authoring: projected
                """).File!)
            .ShouldContain(x => x.Contains("`defaults.scaffold:` must say"));
    }

    [Fact]
    public void an_unknown_owner_token_is_refused_rather_than_left_in_a_type_name()
    {
        SpecOwnershipReader.Validate(SpecOwnershipReader.Read(
                """
                schema: 1
                model: CritterCrush
                defaults:
                  authoring: projected
                  scaffold: true
                  owner: CritterCrush.Specs.{domain}Specs
                """).File!)
            .ShouldContain(x => x.Contains("{domain}"));
    }

    [Fact]
    public void a_literal_default_owner_pointing_several_features_at_one_type_is_refused()
    {
        // The failure mode a template exists to avoid: one type cannot publish two features,
        // because [BobcatFeature] is class-level. Caught for a defaulted owner exactly as it is
        // for a hand-written one.
        var twoFeatures = model();
        twoFeatures.Slices =
        [
            new CuratedSlice
            {
                Name = "A", Specifications = new CuratedSpecifications { Feature = "One" }
            },
            new CuratedSlice
            {
                Name = "B", Specifications = new CuratedSpecifications { Feature = "Two" }
            }
        ];

        var problems = SpecOwnershipReader.Validate(
            SpecOwnershipReader.Read(
                """
                schema: 1
                model: CritterCrush
                defaults:
                  authoring: projected
                  scaffold: true
                  owner: CritterCrush.Specs.AllSpecs
                """).File!,
            twoFeatures);

        problems.ShouldContain(x => x.Contains("covers slices in more than one feature")
                                    && x.Contains("from `defaults.owner:`"));
    }

    [Fact]
    public void defaults_cannot_make_every_slice_unit_tested()
    {
        // `coveredBy:` names one scenario, so it is per slice by nature — a defaulted unit kind
        // would claim one cover for the whole file.
        SpecOwnershipReader.Validate(SpecOwnershipReader.Read(
                """
                schema: 1
                model: CritterCrush
                defaults:
                  kind: unit
                """).File!)
            .ShouldContain(x => x.Contains("`defaults.kind: unit` is not allowed"));
    }
}

public class SpecOwnershipSnifferTests
{
    [Fact]
    public void a_manifest_is_told_from_a_curated_model_by_its_entry_keys()
    {
        EventModelFileSniffer.Sniff(
            """
            schema: 1
            model: CritterCrush
            slices:
              - slice: ProposeHomeCheckAppointment
                kind: unit
            """).ShouldBe(EventModelFileKind.SpecOwnership);
    }

    [Fact]
    public void a_curated_model_still_sniffs_as_curated()
    {
        EventModelFileSniffer.Sniff(
            """
            schema: 1
            model: CritterCrush
            slices:
              - name: ConfirmAppointment
                pattern: Command
            """).ShouldBe(EventModelFileKind.Curated);
    }

    [Fact]
    public void an_ambiguous_slices_list_stays_curated_so_the_new_member_changes_no_existing_answer()
    {
        EventModelFileSniffer.Sniff(
            """
            schema: 1
            model: CritterCrush
            slices: []
            """).ShouldBe(EventModelFileKind.Curated);
    }

    [Fact]
    public void an_emlang_export_is_unaffected()
    {
        EventModelFileSniffer.Sniff(
            """
            slices:
              WithdrawFunds:
                steps: []
            """).ShouldBe(EventModelFileKind.Emlang);
    }
}
