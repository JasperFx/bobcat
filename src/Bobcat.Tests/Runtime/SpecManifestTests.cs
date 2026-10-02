using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #391: what a suite says it specifies, and how a set of identities becomes the command
/// line that suite's own runner understands.
/// </summary>
[Collection("spec-manifest")]
public class SpecManifestTests : IDisposable
{
    private readonly string? _previousPath
        = Environment.GetEnvironmentVariable(SpecManifest.PathVariable);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bobcat-spec-manifest-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SpecManifest.PathVariable, _previousPath);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static SpecManifest gherkin() => new(
        SpecManifest.GherkinLane, SpecManifest.BobcatFramework, "Orders.Specs",
        [new SpecManifestEntry("Orders/places an order"), new SpecManifestEntry("Stock/counts")]);

    private static SpecManifest projected() => new(
        SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "DaemonTests",
        [
            new SpecManifestEntry("Booking appointments/a proposal is confirmed",
                "Booking.Tests.BookingSpecs", "a_proposal_is_confirmed"),
            new SpecManifestEntry("Booking appointments/a proposal is declined",
                "Booking.Tests.BookingSpecs", "a_proposal_is_declined")
        ]);

    // --- The document.

    [Fact]
    public void it_round_trips_through_json()
    {
        // The manifest crosses a process boundary, so the serialized form is the contract and not
        // an implementation detail of either side.
        var read = SpecManifest.FromJson(projected().ToJson()).ShouldNotBeNull();

        read.Lane.ShouldBe("projected");
        read.Framework.ShouldBe("xunit");
        read.Suite.ShouldBe("DaemonTests");
        read.Identities.ShouldBe([
            "Booking appointments/a proposal is confirmed",
            "Booking appointments/a proposal is declined"
        ]);
        read.For("Booking appointments/a proposal is confirmed")!.QualifiedTestMethod
            .ShouldBe("Booking.Tests.BookingSpecs.a_proposal_is_confirmed");
    }

    [Fact]
    public void a_gherkin_entry_carries_no_test_method_because_the_identity_is_the_uid()
    {
        gherkin().For("Orders/places an order")!.QualifiedTestMethod.ShouldBeNull();
    }

    [Fact]
    public void an_identity_the_suite_does_not_have_reads_as_absent_rather_than_throwing()
    {
        gherkin().For("Nope/not here").ShouldBeNull();
    }

    // --- Writing it only when asked.

    [Fact]
    public void nothing_is_written_when_nobody_asked()
    {
        Environment.SetEnvironmentVariable(SpecManifest.PathVariable, null);

        var built = false;
        SpecManifest.WriteIfRequested(() => { built = true; return gherkin(); }).ShouldBeFalse();

        built.ShouldBeFalse("a suite that was not asked should not pay to build the answer");
    }

    [Fact]
    public void it_is_written_where_the_asker_nominated()
    {
        var path = Path.Combine(_directory, "nested", "specs.json");
        Environment.SetEnvironmentVariable(SpecManifest.PathVariable, path);

        SpecManifest.WriteIfRequested(gherkin).ShouldBeTrue();

        SpecManifest.Read(path).Identities.ShouldBe(["Orders/places an order", "Stock/counts"]);
    }

    [Fact]
    public void an_unwritable_path_is_reported_and_never_fails_the_suite()
    {
        // Same invariant as the monitor publisher's probe: a listing is a courtesy to a listener,
        // and a test run that collapsed because one could not be saved would be the worse bug.
        Environment.SetEnvironmentVariable(SpecManifest.PathVariable, _directory);
        Directory.CreateDirectory(_directory);

        SpecManifest.WriteIfRequested(gherkin).ShouldBeFalse();
    }

    // --- Turning identities into a command line.

    [Fact]
    public void a_gherkin_suite_is_filtered_by_the_identity_unchanged()
    {
        // The platform's uid IS the identity here, so there is nothing to translate.
        SpecFilterArguments.For(gherkin(), SpecSelection.Of("Stock/counts"))
            .ShouldBe(["--filter-uid", "Stock/counts"]);
    }

    [Fact]
    public void several_identities_travel_as_one_option()
    {
        SpecFilterArguments
            .For(gherkin(), SpecSelection.Of("Stock/counts", "Orders/places an order"))
            .ShouldBe(["--filter-uid", "Stock/counts", "Orders/places an order"]);
    }

    [Fact]
    public void a_projected_suite_is_filtered_by_the_method_the_identity_was_bound_to()
    {
        // The whole asymmetry of issue #391: a projected uid is the test framework's own, built
        // from assembly, class, method and arguments — nothing a monitor could know.
        SpecFilterArguments
            .For(projected(), SpecSelection.Of("Booking appointments/a proposal is declined"))
            .ShouldBe(["--filter-method", "Booking.Tests.BookingSpecs.a_proposal_is_declined"]);
    }

    [Fact]
    public void narrowing_nothing_needs_no_argument()
    {
        SpecFilterArguments.For(projected(), SpecSelection.Everything).ShouldBeEmpty();
    }

    [Fact]
    public void an_identity_the_suite_does_not_have_is_refused_by_name()
    {
        // Checked before a filter is built, because a framework asked for a test it does not have
        // simply runs nothing — which reads as a passing run.
        var refusal = Should.Throw<ArgumentException>(() => SpecFilterArguments.For(
            gherkin(), SpecSelection.Of("Orders/places an order", "Nope/not here")));

        refusal.Message.ShouldContain("'Nope/not here'");
        refusal.Message.ShouldContain("Orders.Specs");
    }

    [Fact]
    public void tunit_says_it_is_not_supported_rather_than_guessing_a_filter()
    {
        // TUnit filters by tree-node path, and nothing in this repository can run a TUnit host to
        // verify the spelling (TUnit.Engine needs Microsoft.Testing.Platform 2.4.0; src is pinned
        // to 1.9.1). An unverified filter would be the silent whole-suite run that
        // GuardAgainstAnUnfilteredRun exists to prevent.
        var manifest = projected() with { Framework = SpecManifest.TUnitFramework };

        Should.Throw<NotSupportedException>(
                () => SpecFilterArguments.For(manifest, SpecSelection.Of("Booking appointments/a proposal is declined")))
            .Message.ShouldContain("TUnit");
    }

    [Fact]
    public void an_unknown_framework_is_refused_and_lists_the_ones_that_are_known()
    {
        Should.Throw<NotSupportedException>(
                () => SpecFilterArguments.For(
                    projected() with { Framework = "nunit" },
                    SpecSelection.Of("Booking appointments/a proposal is declined")))
            .Message.ShouldContain("'nunit' is not a framework");
    }

    [Fact]
    public void an_entry_with_no_binding_cannot_be_turned_into_a_method_filter()
    {
        var manifest = projected() with
        {
            Specs = [new SpecManifestEntry("Booking appointments/a proposal is confirmed")]
        };

        Should.Throw<NotSupportedException>(
                () => SpecFilterArguments.For(
                    manifest, SpecSelection.Of("Booking appointments/a proposal is confirmed")))
            .Message.ShouldContain("carries no test method");
    }
}
