namespace Bobcat.Acceptance.Tests;

/// <summary>
/// The one fixture in this project whose feature declares Event Modeling slices, so the assembly
/// carries a real generated <c>BobcatEventModelSource</c> — what <c>SpecIdentityAuditTests</c>
/// audits (issue #338). Deliberately trivial: the subject under test is the identities the
/// generator emits, not the arithmetic.
/// </summary>
[FixtureTitle("Slice Identity")]
public class SliceIdentityFixture : Fixture
{
    private int _balance;
    private bool _refused;

    [Given("a wallet with {int}")]
    public void AWalletWith(int balance) => _balance = balance;

    [When("{int} is credited")]
    public void IsCredited(int amount)
    {
        if (amount <= 0)
        {
            _refused = true;
            return;
        }

        _balance += amount;
    }

    [Then("the balance should be {int}")]
    public bool TheBalanceShouldBe(int expected) => _balance == expected;

    [Then("the credit is refused")]
    public bool TheCreditIsRefused() => _refused;
}
