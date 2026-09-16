using Bobcat;
using Bobcat.Generated.EventModel;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// Issue #324, part one: an ordinary xUnit test bound to a slice with <c>[BobcatSlice]</c> reaches
/// the Event Model.
/// </summary>
/// <remarks>
/// Before this, <c>[BobcatFeature]</c> rendered a projected test as a readable specification and
/// carried no slice identity at all — so the slice stayed unbound however green the test was, and
/// no spec-identity gate could join it. These run against the GENERATED
/// <c>BobcatEventModelSource</c>, so the binding is proved end to end rather than at the extractor.
/// </remarks>
[BobcatFeature("Wallet reconciliation")]
[BobcatSlice(SliceName = "ReconcileWallets", Domain = "Wallets", Chapter = "Operations", Pattern = "Automation")]
public class WalletReconciliationSpecs
{
    [Fact]
    public void an_overnight_sweep_reconciles_every_wallet()
    {
        // Given a wallet with a credit
        // When the overnight reconciliation runs
        // Then every wallet balance agrees with its events
        true.ShouldBeTrue();
    }

    /// <summary>
    /// A method-level binding wins over the class's — how one class covers several slices. Bound by
    /// TYPE here, which is the preferred spelling and merges into the slice Wallet.feature already
    /// declares.
    /// </summary>
    [Fact]
    [BobcatSlice(SliceType = typeof(CreditWallet))]
    public void a_reconciliation_leaves_a_credited_wallet_alone()
    {
        // Given a credited wallet
        // Then the reconciliation changes nothing
        true.ShouldBeTrue();
    }
}

public class ProjectedSliceBindingTests
{
    private static EventModelDescriptor describe() => BobcatEventModelSource.Describe();

    private static EventModelSliceDescriptor slice(string name)
        => describe().Slices.Single(s => s.Name == name);

    [Fact]
    public void a_projected_test_declares_a_slice_the_gherkin_lane_never_mentions()
    {
        var reconcile = slice("ReconcileWallets");

        reconcile.Specifications.Select(x => x.Identity)
            .ShouldContain("Wallet reconciliation/an overnight sweep reconciles every wallet");

        // The groupings travel with it, which is what the canvas needs to place the slice at all.
        reconcile.Domain.ShouldBe("Wallets");
        reconcile.Chapter.ShouldBe("Operations");

        // And the pattern, for the same reason @pattern: exists (#323): a projected test's shape
        // cannot express Automation, and guessing Command would disagree with a model that knows.
        reconcile.Pattern.ShouldBe(SlicePattern.Automation);
    }

    [Fact]
    public void a_projected_test_contributes_evidence_but_never_roles()
    {
        var reconcile = slice("ReconcileWallets");

        // Deliberate. The curated model states the roles on the Declared rung and the code states
        // them on Derived; a third opinion from a test could only manufacture a SourceDisagreement
        // nobody can act on. What a projected test contributes is a bound identity.
        reconcile.CommandType.ShouldBeNull();
        reconcile.EmittedEvents.ShouldBeEmpty();
        reconcile.AggregateTypes.ShouldBeEmpty();
        reconcile.ReadModelTypes.ShouldBeEmpty();
    }

    [Fact]
    public void a_method_level_binding_merges_into_a_slice_the_gherkin_lane_owns()
    {
        var credit = slice("CreditWallet");

        // The projected test's identity joins the seven Gherkin scenarios and the code-first one,
        // because slices merge by NAME across every authoring style.
        credit.Specifications.Select(x => x.Identity)
            .ShouldContain("Wallet reconciliation/a reconciliation leaves a credited wallet alone");

        // …and it did not disturb what that slice already knew. Binding only, even when merging.
        credit.CommandType.ShouldNotBeNull();
        credit.Pattern.ShouldBe(SlicePattern.Command);
    }
}
