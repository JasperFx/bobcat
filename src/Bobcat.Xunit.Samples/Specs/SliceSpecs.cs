using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The one class in the samples that binds an Event Modeling slice, so this host carries a real
/// generated <c>BobcatEventModelSource</c>. Bobcat#488: <c>Bobcat.Supervisor.Tests</c> runs this
/// host against a capturing console and checks that the projected run bracket publishes that half.
/// Both specs pass on purpose, unlike most of their neighbours.
/// </summary>
[BobcatFeature("Wallet"), BobcatScenario]
[BobcatSlice(SliceName = "CreditWallet", Domain = "Samples")]
public class SliceSpecs
{
    private int _balance;

    [Fact]
    public void a_credit_raises_the_balance()
    {
        // Given a wallet with 100
        _balance = 100;

        // When 25 is credited
        _balance += 25;

        // Then the balance is 125
        _balance.ShouldBe(125);
    }

    [Fact]
    public void a_second_credit_adds_to_the_first()
    {
        // Given a wallet with 100
        _balance = 100;

        // When 25 is credited twice
        _balance += 25;
        _balance += 25;

        // Then the balance is 150
        _balance.ShouldBe(150);
    }
}
