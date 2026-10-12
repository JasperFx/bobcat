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

        manifest.Specs.Count.ShouldBe(
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

    [Fact]
    public Task an_identity_the_suite_does_not_have_is_refused_before_anything_is_launched()
        => _harness.RefusesAnIdentityItDoesNotHave(projectedHost, "Calculator/no such specification");
}
