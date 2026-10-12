using Bobcat.Runtime;
using Bobcat.SpecIdentityChecks;
using Shouldly;

namespace Bobcat.Mtp.Tests;

// Phase 3: delete with the Gherkin lane (bobcat#471).

/// <summary>
/// Issue #391 for the Gherkin lane: "run this specification" against a real Bobcat MTP host,
/// listed and then run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The projected half lives in <c>Bobcat.Xunit.Tests/SpecIdentity/ProjectedSpecIdentityEndToEndTests</c>.</b>
/// These used to be one class, deliberately: pinning both lanes with one small corpus is what keeps
/// them from drifting, the same reasoning as <c>MarkerSpecNamingAgreementTests</c> and
/// <c>SliceTagParsingAgreementTests</c>. Phase 2 of bobcat#471 moved the projected half out so it
/// survives this project's deletion, and kept the "agree from one place" part by sharing the
/// assertions instead: both halves go through <see cref="SpecIdentityHarness"/>, which lives in
/// <c>Bobcat.Xunit.Tests</c> and is linked into this project. Add a rule there, not here.
/// </para>
/// </remarks>
public class SpecIdentityEndToEndTests : IDisposable
{
    private static readonly string gherkinHost = SpecIdentityHarness.HostIn("Bobcat.Mtp.GeneratedHost");

    private readonly SpecIdentityHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task a_gherkin_suite_lists_its_identities_without_running_anything()
    {
        var manifest = await _harness.List(gherkinHost);

        manifest.Lane.ShouldBe(SpecManifest.GherkinLane);
        manifest.Framework.ShouldBe(SpecManifest.BobcatFramework);
        manifest.Suite.ShouldBe("Bobcat.Mtp.GeneratedHost");

        manifest.Identities.ShouldBe([
            "Ordering/An order can be emptied",
            "Ordering/An order is accepted",
            "Shipping/A shipment is labelled"
        ]);

        // The identity IS the platform uid here, so there is nothing for a filter to translate.
        manifest.Specs.ShouldAllBe(spec => spec.QualifiedTestMethod == null);
    }

    [Fact]
    public Task nothing_is_written_when_nothing_asked()
        => _harness.NothingIsWrittenWhenNothingAsked(gherkinHost);

    [Fact]
    public Task a_gherkin_suite_runs_one_specification_named_by_identity()
        => _harness.RunsExactly(
            gherkinHost,
            ["Ordering/An order is accepted"],
            ["--filter-uid", "Ordering/An order is accepted"]);

    [Fact]
    public Task several_identities_run_together()
        => _harness.RunsExactly(gherkinHost, ["Ordering/An order is accepted", "Shipping/A shipment is labelled"]);

    [Fact]
    public Task an_identity_the_suite_does_not_have_is_refused_before_anything_is_launched()
        => _harness.RefusesAnIdentityItDoesNotHave(gherkinHost, "Ordering/no such specification");
}
