using Bobcat.Xunit.Samples.Application;
using Bobcat.Xunit.Samples.Grammars;
using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The specifications the <c>docs/tutorials/specifications-with-code.md</c> tutorial writes, in the
/// order the page writes them: the test as most teams would write it, the same test narrated with
/// marker comments, the vocabulary, a decision table, a set verification and a theory. The ones
/// with a wrong expectation fail on purpose, like the rest of this corpus, because the page shows
/// what each failure renders as.
/// </summary>
/// <remarks>
/// This project references Bobcat by project rather than by package, so it states
/// <c>[BobcatScenario]</c> itself; a package consumer on xUnit needs only <c>[BobcatFeature]</c>.
/// </remarks>
[BobcatFeature("Withdrawals"), BobcatScenario]
public class WithdrawalSpecs
{
    private readonly AccountSteps _steps = new();

    /// <summary>Step 1, before: an ordinary test with Shouldly and no narrative.</summary>
    // #region withdrawal-as-written
    [Fact]
    public void a_withdrawal_larger_than_the_balance_is_refused_as_most_teams_write_it()
    {
        var account = new Account();
        account.Deposit(100);

        var accepted = account.TryWithdraw(200);

        accepted.ShouldBeFalse();
        account.Balance.ShouldBe(100);
    }
    // #endregion withdrawal-as-written

    /// <summary>Step 1, after: the same body, with marker comments declaring its steps.</summary>
    // #region withdrawal-narrated
    [Fact]
    public void a_withdrawal_larger_than_the_balance_is_refused_narrated()
    {
        // Given an account with a balance of 100
        var account = new Account();
        account.Deposit(100);

        // When 200 is withdrawn
        var accepted = account.TryWithdraw(200);

        // Then the withdrawal is refused and the balance is still 100
        accepted.ShouldBeFalse();
        account.Balance.ShouldBe(100);
    }
    // #endregion withdrawal-narrated

    /// <summary>Step 2: the same specification as four calls into the vocabulary.</summary>
    // #region withdrawal-with-steps
    [Fact]
    public void a_withdrawal_larger_than_the_balance_is_refused()
    {
        _steps.AnAccountWithABalanceOf(100);
        _steps.IsWithdrawn(200);
        _steps.TheWithdrawalIs("refused");
        _steps.TheBalanceIs(100);
    }
    // #endregion withdrawal-with-steps

    /// <summary>
    /// Step 2, failing: both expectations are wrong, and both are reported, because
    /// <see cref="SpecAssert"/> gathers
    /// instead of throwing.
    /// </summary>
    // #region withdrawal-wrong-expectation
    [Fact]
    public void a_withdrawal_within_the_balance_is_refused()
    {
        _steps.AnAccountWithABalanceOf(100);
        _steps.IsWithdrawn(30);
        _steps.TheWithdrawalIs("refused");
        _steps.TheBalanceIs(100);
    }
    // #endregion withdrawal-wrong-expectation

    /// <summary>Step 3: the boundary as a decision table.</summary>
    // #region overdraft-boundary
    [Fact]
    public Task the_overdraft_boundary() => _steps.WithdrawalsAreDecidedAs("""
        | balance | amount | outcome  |
        | 100     | 99.99  | accepted |
        | 100     | 100    | accepted |
        | 100     | 100.01 | refused  |
        """);
    // #endregion overdraft-boundary

    /// <summary>Step 3, failing: one row expects the wrong answer, and the grid says which.</summary>
    // #region overdraft-boundary-wrong-row
    [Fact]
    public Task the_overdraft_boundary_with_a_wrong_row() => _steps.WithdrawalsAreDecidedAs("""
        | balance | amount | outcome  |
        | 100     | 99.99  | accepted |
        | 100     | 100    | refused  |
        | 100     | 100.01 | refused  |
        """);
    // #endregion overdraft-boundary-wrong-row

    /// <summary>Step 4: the history as a set verification.</summary>
    // #region history
    [Fact]
    public void the_history_records_what_happened()
    {
        _steps.AnAccountWithABalanceOf(100);
        _steps.IsWithdrawn(30);
        _steps.IsWithdrawn(200);

        _steps.TheTransactionsAre("""
            | Kind       | Amount |
            | Deposit    | 100    |
            | Withdrawal | 30     |
            """);
    }
    // #endregion history

    /// <summary>
    /// Step 4, failing: the specification forgets the withdrawal that happened, so the system's row
    /// is EXTRA.
    /// </summary>
    // #region history-extra-row
    [Fact]
    public void the_history_the_specification_did_not_expect()
    {
        _steps.AnAccountWithABalanceOf(100);
        _steps.IsWithdrawn(30);

        _steps.TheTransactionsAre("""
            | Kind    | Amount |
            | Deposit | 100    |
            """);
    }
    // #endregion history-extra-row

    /// <summary>Step 5: one specification per example row.</summary>
    // #region withdrawal-examples
    [BobcatTheory]
    [BobcatExamples("""
        | balance | amount | outcome  |
        | 100     | 100    | accepted |
        | 100     | 100.01 | refused  |
        """)]
    public void a_withdrawal_is_decided(decimal balance, decimal amount, string outcome)
    {
        _steps.AnAccountWithABalanceOf(balance);
        _steps.IsWithdrawn(amount);
        _steps.TheWithdrawalIs(outcome);
    }
    // #endregion withdrawal-examples
}
