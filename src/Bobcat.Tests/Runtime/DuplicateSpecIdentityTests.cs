using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// bobcat#489, the run-time half: identities two tests claim that the generator could not see
/// together (specs in two assemblies loaded into one process, or BOBCAT036 suppressed) are recorded
/// by the binding, carried on the manifest, reported on standard error, and refused by name when a
/// run asks for one.
/// </summary>
[Collection("spec-manifest")]
public class DuplicateSpecIdentityTests : IDisposable
{
    // A uid of this class's own, removed afterwards: DeclaredSteps is process-wide and test classes
    // run in parallel, so nothing here may clear what another class registered.
    private readonly string _shared = "Duplicates " + Guid.NewGuid().ToString("n") + "/an order is accepted";
    private readonly string _single = "Duplicates " + Guid.NewGuid().ToString("n") + "/an order is rejected";

    private readonly string? _previousPath = Environment.GetEnvironmentVariable(SpecManifest.PathVariable);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bobcat-duplicates-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        DeclaredSteps.Remove(_shared);
        DeclaredSteps.Remove(_single);
        Environment.SetEnvironmentVariable(SpecManifest.PathVariable, _previousPath);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private SpecManifestDuplicate? ours(SpecManifest manifest)
        => manifest.Duplicates?.SingleOrDefault(x => x.Identity == _shared);

    [Fact]
    public void a_second_test_binding_a_bound_identity_is_recorded_rather_than_overwriting_the_first()
    {
        DeclaredSteps.Bind(_shared, "Shop.OrderingSpecs", "an_order_is_accepted");
        DeclaredSteps.Bind(_shared, "Shop.Other.OrderingSpecs", "an_order_is_accepted");

        // The first binding stays: before #489 the listing silently named whichever bound last
        DeclaredSteps.BindingFor(_shared)!.QualifiedTestMethod.ShouldBe("Shop.OrderingSpecs.an_order_is_accepted");

        var duplicate = DeclaredSteps.Duplicates.Single(x => x.Identity == _shared);
        duplicate.Claims.Select(x => x.QualifiedTestMethod).ShouldBe([
            "Shop.OrderingSpecs.an_order_is_accepted",
            "Shop.Other.OrderingSpecs.an_order_is_accepted"
        ]);
    }

    [Fact]
    public void the_same_test_binding_twice_is_not_a_duplicate()
    {
        // An assembly loaded twice in one process runs its module initializer twice
        DeclaredSteps.Bind(_single, "Shop.OrderingSpecs", "an_order_is_rejected");
        DeclaredSteps.Bind(_single, "Shop.OrderingSpecs", "an_order_is_rejected");

        DeclaredSteps.Duplicates.ShouldNotContain(x => x.Identity == _single);
    }

    [Fact]
    public void the_manifest_carries_the_duplicates_and_round_trips_them_through_json()
    {
        DeclaredSteps.Bind(_shared, "Shop.OrderingSpecs", "an_order_is_accepted");
        DeclaredSteps.Bind(_shared, "Shop.Other.OrderingSpecs", "an_order_is_accepted");
        DeclaredSteps.Bind(_single, "Shop.OrderingSpecs", "an_order_is_rejected");

        var manifest = SpecManifest.FromJson(DeclaredSteps.Manifest("xunit", "Shop.Specs").ToJson()).ShouldNotBeNull();

        // Specs stays a set, one entry per identity, so an older reader keeps reading it as one
        manifest.Specs.Count(x => x.Identity == _shared).ShouldBe(1);
        ours(manifest).ShouldNotBeNull().Claims.Count.ShouldBe(2);

        manifest.IsAmbiguous(_shared).ShouldBeTrue();
        manifest.IsAmbiguous(_single).ShouldBeFalse();
    }

    [Fact]
    public void a_manifest_with_no_duplicates_says_nothing_about_them()
    {
        var manifest = new SpecManifest(
            SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "Shop.Specs",
            [new SpecManifestEntry(_single, "Shop.OrderingSpecs", "an_order_is_rejected")]);

        manifest.ToJson().ShouldNotContain("\"duplicates\":");
        manifest.DescribeDuplicates().ShouldBeEmpty();
    }

    [Fact]
    public void the_write_reports_each_duplicate_on_standard_error_and_still_writes_the_manifest()
    {
        DeclaredSteps.Bind(_shared, "Shop.OrderingSpecs", "an_order_is_accepted");
        DeclaredSteps.Bind(_shared, "Shop.Other.OrderingSpecs", "an_order_is_accepted");
        DeclaredSteps.Bind(_single, "Shop.OrderingSpecs", "an_order_is_rejected");

        var path = Path.Combine(_directory, "specs.json");
        Environment.SetEnvironmentVariable(SpecManifest.PathVariable, path);

        var errors = new StringWriter();
        SpecManifest.WriteIfRequested(() => DeclaredSteps.Manifest("xunit", "Shop.Specs"), errors).ShouldBeTrue();

        var reported = errors.ToString();
        reported.ShouldContain($"the spec identity '{_shared}' in Shop.Specs is claimed by 2 tests: "
                               + "Shop.OrderingSpecs.an_order_is_accepted, Shop.Other.OrderingSpecs.an_order_is_accepted");
        reported.ShouldContain("[BobcatSpec(Name = \"...\")]");
        reported.ShouldNotContain(_single);

        // Written anyway: every other specification keeps its listing
        var written = SpecManifest.Read(path);
        written.Identities.ShouldContain(_single);
        ours(written).ShouldNotBeNull();
    }

    [Fact]
    public void running_a_duplicated_identity_by_name_is_refused()
    {
        var manifest = new SpecManifest(
            SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "Shop.Specs",
            [
                new SpecManifestEntry(_shared, "Shop.OrderingSpecs", "an_order_is_accepted"),
                new SpecManifestEntry(_single, "Shop.OrderingSpecs", "an_order_is_rejected")
            ])
        {
            Duplicates =
            [
                new SpecManifestDuplicate(_shared,
                [
                    new SpecManifestEntry(_shared, "Shop.OrderingSpecs", "an_order_is_accepted"),
                    new SpecManifestEntry(_shared, "Shop.Other.OrderingSpecs", "an_order_is_accepted")
                ])
            ]
        };

        Should.Throw<ArgumentException>(() => SpecFilterArguments.For(manifest, SpecSelection.Of(_shared)))
            .Message.ShouldContain($"more than one test claiming '{_shared}'");

        // Only the duplicated identity is refused; the rest of the suite still runs by name
        SpecFilterArguments.For(manifest, SpecSelection.Of(_single))
            .ShouldBe(["--filter-method", "Shop.OrderingSpecs.an_order_is_rejected"]);
    }
}
