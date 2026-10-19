using Bobcat.Xunit;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// The projected twin of <c>SliceIdentity.feature</c>: the one class in this project that binds
/// Event Modeling slices. Its identity audit (<c>SpecIdentityAuditTests</c>) moved to the Wolverine repo with
/// <c>SpecIdentityAudit</c> in bobcat#744; the class stays as the projected twin of the feature. Deliberately trivial: the subject under test is
/// the identities the generator emits, not the arithmetic.
/// </summary>
/// <remarks>
/// The Gherkin lane's step-less scenario is a pending claim; here it is
/// <c>[BobcatSpec(Pending = true)]</c>, which is skipped by design and still a claim on its slice.
/// </remarks>
[BobcatFeature("Slice Identity")]
[BobcatSlice(SliceName = "CreditWallet", Domain = "Acceptance", Chapter = "Identity")]
public class SliceIdentitySpecs
{
    private int _balance;
    private bool _refused;

    private void credit(int amount)
    {
        if (amount <= 0)
        {
            _refused = true;
            return;
        }

        _balance += amount;
    }

    [Fact]
    public void A_credited_wallet_shows_the_new_balance()
    {
        // Given a wallet with 100
        _balance = 100;

        // When 25 is credited
        credit(25);

        // Then the balance should be 125
        _balance.ShouldBe(125);
    }

    [Fact]
    public void Crediting_nothing_is_refused()
    {
        // Given a wallet with 100
        _balance = 100;

        // When 0 is credited
        credit(0);

        // Then the credit is refused
        _refused.ShouldBeTrue();
    }

    [BobcatSpec(Pending = true)]
    [BobcatSlice(SliceName = "WalletBalance", Domain = "Acceptance", Chapter = "Identity")]
    public void The_balance_view_is_not_specified_yet()
    {
    }
}
