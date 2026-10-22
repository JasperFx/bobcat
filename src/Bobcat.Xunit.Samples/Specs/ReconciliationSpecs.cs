using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The <c>docs/marker-steps.md</c> example of keywordless steps (issue #324): a <c>// *</c> bullet is a
/// step with no Given/When/Then, Gauge-style. Green.
/// </summary>
[BobcatFeature("Overnight reconciliation")]
public class ReconciliationSpecs
{
    private readonly Dictionary<string, List<decimal>> _credits = new();
    private readonly Dictionary<string, decimal> _balances = new();

    // #region bullet-steps
    [Fact]
    public async Task the_overnight_sweep_reconciles_every_wallet()
    {
        // * two wallets, one of them credited twice
        await Seed();

        // * the overnight reconciliation runs
        await Sweep();

        // Then every balance agrees with its events
        await AssertBalances();
    }
    // #endregion bullet-steps

    private Task Seed()
    {
        _credits["ann"] = [25m, 75m];
        _credits["bob"] = [40m];
        return Task.CompletedTask;
    }

    private Task Sweep()
    {
        foreach (var (wallet, credits) in _credits) _balances[wallet] = credits.Sum();
        return Task.CompletedTask;
    }

    private Task AssertBalances()
    {
        _balances["ann"].ShouldBe(100m);
        _balances["bob"].ShouldBe(40m);
        return Task.CompletedTask;
    }
}
