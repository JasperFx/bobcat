using System.Text.RegularExpressions;
using Bobcat.Runtime;
using Bobcat.SpecIdentityChecks;
using Shouldly;
using Xunit;

namespace Bobcat.Xunit.Tests.SpecIdentity;

/// <summary>
/// Issue #391 for the projected lane: "run this specification" against a real xUnit v3 host,
/// <c>Bobcat.Xunit.Samples</c>, listed and then run.
/// </summary>
/// <remarks>
/// The projected half of what was one class in <c>Bobcat.Mtp.Tests</c>, moved here in Phase 2 of
/// bobcat#471 so it outlived the Gherkin lane. Its rules live in <see cref="SpecIdentityHarness"/>.
/// </remarks>
public class ProjectedSpecIdentityEndToEndTests : IDisposable
{
    private static readonly string projectedHost = SpecIdentityHarness.HostIn("Bobcat.Xunit.Samples");

    private readonly SpecIdentityHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task a_projected_suite_lists_its_identities_and_the_method_each_one_is()
    {
        var manifest = await _harness.List(projectedHost);

        manifest.Lane.ShouldBe(SpecManifest.ProjectedLane);
        manifest.Framework.ShouldBe(SpecManifest.XunitFramework);
        manifest.Suite.ShouldBe("Bobcat.Xunit.Samples");

        manifest.For("Calculator/using sentences").ShouldNotBeNull()
            .QualifiedTestMethod
            .ShouldBe("Bobcat.Xunit.Samples.Specs.CalculatorSpecs.using_sentences");
    }

    [Fact]
    public async Task a_projected_listing_covers_every_specification_however_its_steps_are_declared()
    {
        // The gap that made the first cut of this wrong. A projected spec declares its steps by
        // marker comments, by [BobcatStep] interceptors, or by grammar calls, and only the first
        // reaches DeclaredSteps.Register — so a listing keyed on that set named 8 of this suite's
        // 41 specifications while the other 33 rendered and published verdicts perfectly well.
        // What makes a test a specification is that [BobcatScenario] records it.
        var manifest = await _harness.List(projectedHost);
        var (_, output) = await SpecIdentityHarness.Run(projectedHost, null, "--list-tests");

        // The count comes from the platform rather than from a literal, because the claim is that
        // the listing covers EVERY specification — so the right number is whatever the suite has,
        // and the test should hold as the sample corpus grows. Two literals had to be edited the
        // first time it did (adding the VerifyObject samples took it to 45), and that edit is
        // indistinguishable from someone quietly relaxing the assertion to match a regression.
        var found = int.Parse(Regex.Match(output, @"found (\d+) test\(s\)").Groups[1].Value);

        found.ShouldBeGreaterThan(
            8,
            "the suite should have far more specifications than the 8 that declare marker steps, "
            + "or this test cannot tell the two rules apart");

        // A data-driven specification is listed once for the method and once per constant row
        // (bobcat#569): the Event Model keys on the method, the platform runs the rows. So the
        // method's own entry is not a test the platform lists, and is set aside here.
        var theories = manifest.Specs.Where(x => x.IsRow).Select(x => x.Parent).Distinct().Count();

        (manifest.Specs.Count - theories).ShouldBe(
            found,
            "every test [BobcatScenario] records is a specification, however its steps are declared");
    }

    [Fact]
    public Task nothing_is_written_when_nothing_asked()
        => _harness.NothingIsWrittenWhenNothingAsked(projectedHost);

    [Fact]
    public Task a_projected_suite_runs_one_specification_named_by_the_same_kind_of_identity()
        // The monitor sent an identity, exactly as it would to the Gherkin host. The translation
        // to this framework's own method filter happened here, from the suite's own manifest.
        => _harness.RunsExactly(
            projectedHost,
            ["Calculator/using sentences"],
            ["--filter-method", "Bobcat.Xunit.Samples.Specs.CalculatorSpecs.using_sentences"]);

    [Fact]
    public Task several_identities_run_together()
        // "asserting values" is red on purpose — the samples show how a wrong value renders — so
        // the claim here is only that exactly the two named specifications ran.
        => _harness.RunsExactly(
            projectedHost, ["Calculator/using sentences", "Calculator/asserting values"], requireGreen: false);

    private const string explicitlyNamed =
        "Facts, in one attribute/a spec's name can't always be a method name -- re-stated, verbatim";

    [Fact]
    public async Task an_explicit_scenario_name_is_the_identity_the_manifest_lists()
    {
        // bobcat#489: [BobcatSpec(Name = "…")] is the scenario half of the identity, punctuation
        // kept, and the binding still names the method a filter can ask for
        var manifest = await _harness.List(projectedHost);

        manifest.For(explicitlyNamed).ShouldNotBeNull()
            .QualifiedTestMethod
            .ShouldBe("Bobcat.Xunit.Samples.Specs.OneAttributeSpecs.named_explicitly");

        manifest.Identities.ShouldNotContain("Facts, in one attribute/named explicitly");
        manifest.Duplicates.ShouldBeNull("the sample corpus must not collide with itself");
    }

    [Fact]
    public Task an_explicitly_named_specification_runs_by_its_name_and_records_under_it()
        // The sample asserts ScenarioRecorder.Current.Uid inside its body, so a green run is the
        // recorder publishing the same string the generator bound and the manifest listed
        => _harness.RunsExactly(
            projectedHost,
            [explicitlyNamed],
            ["--filter-method", "Bobcat.Xunit.Samples.Specs.OneAttributeSpecs.named_explicitly"]);

    private const string coordinateSpec = "Facts, in one attribute/its coordinates are its traits";

    [Fact]
    public async Task the_manifest_carries_each_specification_s_coordinates_and_declared_traits()
    {
        // bobcat#573: what `bobcat run --slice` resolves through
        var entry = (await _harness.List(projectedHost)).For(coordinateSpec);

        entry.ShouldNotBeNull();
        entry.Slice.ShouldBe("Selecting by coordinates");
        entry.Chapter.ShouldBe("Selection");
        entry.Domain.ShouldBe("Bobcat");
        entry.Pattern.ShouldBeNull();
        entry.Traits!["Category"].ShouldBe(["Coordinates"]);
    }

    [Fact]
    public async Task the_platform_s_own_trait_filter_selects_a_specification_by_its_slice()
    {
        // bobcat#573: plain `dotnet test --filter-trait`, no Bobcat in the loop. The spec asserts its
        // own traits inside its body, so green is the real runner having them.
        var (exitCode, output) = await SpecIdentityHarness.Run(
            projectedHost, null, "--filter-trait", "Slice=Selecting by coordinates");

        exitCode.ShouldBe(0, output);
        output.ShouldContain("total: 1");
    }

    [Fact]
    public async Task an_inherited_specification_is_listed_once_per_subclass_under_the_subclass()
    {
        // bobcat#600: the test is written once on an abstract generic base. The base is no
        // specification of its own; each subclass is, under its own feature.
        var manifest = await _harness.List(projectedHost);

        manifest.For("Alpha transport/each subclass is its own feature").ShouldNotBeNull()
            .QualifiedTestMethod
            .ShouldBe("Bobcat.Xunit.Samples.Specs.AlphaTransportCompliance.each_subclass_is_its_own_feature");

        manifest.For("Beta Transport Compliance/each subclass is its own feature").ShouldNotBeNull()
            .QualifiedTestMethod
            .ShouldBe("Bobcat.Xunit.Samples.Specs.BetaTransportCompliance.each_subclass_is_its_own_feature");

        manifest.Identities.ShouldNotContain("Shared compliance/each subclass is its own feature");
    }

    [Fact]
    public Task an_inherited_specification_runs_by_its_subclass_identity_and_records_under_it()
        // The sample asserts the recording's feature and declared steps inside its body
        => _harness.RunsExactly(
            projectedHost,
            ["Beta Transport Compliance/each subclass is its own feature"],
            ["--filter-method", "Bobcat.Xunit.Samples.Specs.BetaTransportCompliance.each_subclass_is_its_own_feature"]);

    [Fact]
    public Task an_identity_the_suite_does_not_have_is_refused_before_anything_is_launched()
        => _harness.RefusesAnIdentityItDoesNotHave(projectedHost, "Calculator/no such specification");
}
