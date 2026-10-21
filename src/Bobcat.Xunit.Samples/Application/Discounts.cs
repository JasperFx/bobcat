namespace Bobcat.Xunit.Samples.Application;

/// <summary>
/// The system under test for the data-row examples on <c>docs/spec-identities.md</c>: a percentage
/// off by customer tier, so a theory has three rows worth naming three different ways.
/// </summary>
// #region discounts
public enum Tier { Standard, Silver, Gold }

public static class Discounts
{
    public static int PercentOff(Tier tier) => tier switch
    {
        Tier.Gold => 20,
        Tier.Silver => 10,
        _ => 0
    };

    public static int Apply(int amount, Tier tier) => amount - amount * PercentOff(tier) / 100;
}
// #endregion discounts
