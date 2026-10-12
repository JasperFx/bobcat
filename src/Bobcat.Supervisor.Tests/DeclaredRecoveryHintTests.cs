using Bobcat.Resilience;
using Bobcat.Runtime;
using JasperFx.Testing;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #469: the recovery hints a worker declares on itself reach the supervisor through the
/// spec manifest discovery asks for, and change what the failure policy decides.
/// </summary>
public class DeclaredRecoveryHintTests
{
    private const string Orders = "Orders.Tests.OrderSpecs";
    private const string Stock = "Orders.Tests.StockSpecs";

    private static SpecManifest manifest(params SpecManifestHint[] hints) => new(
        SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "Orders.Tests", [], hints);

    private static SpecManifestHint inClass(string testClass, string failureType, DispositionKind kind) =>
        new(failureType, kind, HintScope.Group, testClass[(testClass.LastIndexOf('.') + 1)..])
        {
            TestClass = testClass
        };

    private static SpecManifestHint runWide(string failureType, DispositionKind kind) =>
        new(failureType, kind, HintScope.Global, "Orders.Tests");

    private static FakeWorkerFactory flakyTimeouts(SpecManifest? declared, params WorkerTest[] tests) => new()
    {
        Tests = tests,
        Manifest = declared,
        ErrorType = (_, _) => "System.TimeoutException",
        Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Error
    };

    private static Supervisor supervised(FakeWorkerFactory factory) => new(factory)
    {
        RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 }
    };

    [Fact]
    public async Task a_class_hint_the_worker_declares_retries_that_class_and_only_that_class()
    {
        var factory = flakyTimeouts(
            manifest(inClass(Orders, "System.TimeoutException", DispositionKind.RetryInProcess)),
            FakeWorkerFactory.InClass(Orders, "places_an_order"),
            FakeWorkerFactory.InClass(Stock, "counts_stock"));

        var results = await supervised(factory).Run();

        results.Tests.Single(t => t.Uid.StartsWith(Orders)).Outcome.ShouldBe(RunOutcome.PassOnRetry);

        // Nothing is declared for StockSpecs and nothing is tagged, so the default stands.
        results.Tests.Single(t => t.Uid.StartsWith(Stock)).AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task discovery_location_joins_a_hint_to_an_opaque_uid()
    {
        // xUnit's uid is a hash, and a custom display name need not say the class either. The
        // MTP location is what places the test, so that is what the join prefers.
        var test = new WorkerTest("9F2C51A7", "places an order")
        {
            TestClass = Orders,
            TestMethod = "places_an_order"
        };

        var factory = flakyTimeouts(
            manifest(inClass(Orders, "System.TimeoutException", DispositionKind.RetryInProcess)), test);

        var results = await supervised(factory).Run();

        results.Tests.Single().Outcome.ShouldBe(RunOutcome.PassOnRetry);
    }

    [Fact]
    public async Task hand_registered_hints_merge_with_the_workers_and_the_narrowest_still_wins()
    {
        var factory = flakyTimeouts(
            manifest(inClass(Orders, "System.TimeoutException", DispositionKind.RetryInProcess)),
            FakeWorkerFactory.InClass(Orders, "places_an_order"),
            FakeWorkerFactory.InClass(Stock, "counts_stock"));

        var supervisor = supervised(factory);

        // Run-wide, by hand: timeouts never recover. The worker's class hint is narrower, so it
        // wins for OrderSpecs; StockSpecs declares nothing, so the hand-registered default speaks.
        var byHand = new RecoveryHint
        {
            FailureTypeName = "System.TimeoutException",
            Kind = DispositionKind.FailAndContinue,
            Because = "the run configuration says so",
            Source = "run configuration"
        };
        supervisor.RecoveryHints.Add(byHand);

        var results = await supervisor.Run();

        results.Tests.Single(t => t.Uid.StartsWith(Orders)).Outcome.ShouldBe(RunOutcome.PassOnRetry);

        var stock = results.Tests.Single(t => t.Uid.StartsWith(Stock));
        stock.AttemptCount.ShouldBe(1);
        stock.Attempts[0].Disposition.Hint.ShouldBeSameAs(byHand);

        supervisor.RecoveryHints.Hints.Count.ShouldBe(2);
        supervisor.RecoveryHints.Hints[0].ShouldBeSameAs(byHand, "hand-registered hints come first and win ties");
    }

    [Fact]
    public async Task a_base_class_hint_from_the_worker_abstains_over_the_wire()
    {
        // The documented degradation. The worker reports one type name, so a class hint naming
        // System.Exception cannot see that a TimeoutException is one. It abstains, and the
        // run-wide hint naming the exact type is the one that applies.
        var factory = flakyTimeouts(
            manifest(
                runWide("System.TimeoutException", DispositionKind.RetryInProcess),
                inClass(Orders, "System.Exception", DispositionKind.FailAndContinue)),
            FakeWorkerFactory.InClass(Orders, "places_an_order"));

        var results = await supervised(factory).Run();

        var test = results.Tests.Single();
        test.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        test.Attempts[0].Disposition.Hint!.Scope.ShouldBe(HintScope.Global);
    }

    [Fact]
    public async Task a_second_run_replaces_the_workers_hints_rather_than_stacking_them()
    {
        var factory = flakyTimeouts(
            manifest(inClass(Orders, "System.TimeoutException", DispositionKind.RetryInProcess)),
            FakeWorkerFactory.InClass(Orders, "places_an_order"));

        var supervisor = supervised(factory);

        await supervisor.Run();
        await supervisor.Run();

        supervisor.RecoveryHints.Hints.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task reading_the_workers_hints_can_be_turned_off()
    {
        var factory = flakyTimeouts(
            manifest(inClass(Orders, "System.TimeoutException", DispositionKind.RetryInProcess)),
            FakeWorkerFactory.InClass(Orders, "places_an_order"));

        var supervisor = supervised(factory);
        supervisor.ReadWorkerRecoveryHints = false;

        var results = await supervisor.Run();

        results.Tests.Single().AttemptCount.ShouldBe(1);
        supervisor.RecoveryHints.IsEmpty.ShouldBeTrue();
        factory.Launched[0].Launch.Environment.ShouldBeNull();
    }

    [Fact]
    public async Task only_the_discovery_worker_is_asked_for_a_manifest()
    {
        var factory = flakyTimeouts(null,
            FakeWorkerFactory.InClass(Orders, "places_an_order"));

        await supervised(factory).Run();

        factory.Launched[0].Launch.Purpose.ShouldBe(WorkerPurpose.Discovery);
        factory.Launched[0].Launch.Environment.ShouldNotBeNull().ShouldContainKey(SpecManifest.PathVariable);

        // A lane is running tests, and a run is not a listing.
        factory.Launched.Skip(1).ShouldAllBe(w => w.Launch.Environment == null);
    }

    [Theory]
    [InlineData("Orders.Tests.OrderSpecs.places_an_order", "Orders.Tests.OrderSpecs", "places_an_order")]
    [InlineData("Orders.Tests.OrderSpecs.totals(amount: 3)", "Orders.Tests.OrderSpecs", "totals")]
    [InlineData("places an order", null, null)]
    public void without_a_location_the_display_name_places_the_test(
        string displayName, string? testClass, string? testMethod)
    {
        Supervisor.PlaceOf(new WorkerTest("uid", displayName)).ShouldBe((testClass, testMethod));
    }
}
