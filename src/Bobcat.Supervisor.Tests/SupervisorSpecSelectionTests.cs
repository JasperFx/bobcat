using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#546: <see cref="Supervisor.Specifications"/> narrows a supervised run by specification
/// identity, joined to the platform's uids through the worker's own manifest — how
/// <c>bobcat run --feature … --retries 2</c> runs only what it was asked for.
/// </summary>
public class SupervisorSpecSelectionTests
{
    private const string Orders = "Orders.Tests.OrderSpecs";

    private static readonly SpecManifest manifest = new(
        SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "Orders.Tests",
        [
            new SpecManifestEntry("Orders/places an order", Orders, "places_an_order"),
            new SpecManifestEntry("Orders/cancels an order", Orders, "cancels_an_order")
        ]);

    private static FakeWorkerFactory factory(SpecManifest? declared) => new()
    {
        Tests =
        [
            FakeWorkerFactory.InClass(Orders, "places_an_order"),
            FakeWorkerFactory.InClass(Orders, "cancels_an_order")
        ],
        Manifest = declared,
        Outcome = (_, _, _) => WorkerTestState.Passed
    };

    [Fact]
    public async Task only_the_selected_specifications_run()
    {
        var results = await new Supervisor(factory(manifest))
        {
            Specifications = SpecSelection.Of("Orders/cancels an order")
        }.Run();

        results.AbortReason.ShouldBeNull();
        results.Tests.Select(x => x.Uid).ShouldBe([$"{Orders}.cancels_an_order"]);
    }

    [Fact]
    public async Task an_identity_no_test_runs_aborts_the_run_naming_it()
    {
        var results = await new Supervisor(factory(manifest))
        {
            Specifications = SpecSelection.Of("Orders/places an order", "Orders/renamed since")
        }.Run();

        results.AbortReason.ShouldNotBeNull().ShouldContain("Orders/renamed since");
        results.Tests.ShouldBeEmpty();
        results.ExitCode.ShouldBe(2);
    }

    [Fact]
    public async Task a_selection_without_a_manifest_is_refused_rather_than_running_everything()
    {
        var results = await new Supervisor(factory(null))
        {
            Specifications = SpecSelection.Of("Orders/places an order")
        }.Run();

        results.AbortReason.ShouldNotBeNull().ShouldContain("no spec manifest");
        results.Tests.ShouldBeEmpty();
    }

    [Fact]
    public async Task no_selection_runs_everything()
    {
        var results = await new Supervisor(factory(manifest)).Run();

        results.Tests.Count.ShouldBe(2);
    }
}
