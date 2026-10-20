using Bobcat.Runtime;
using Bobcat.Xunit.Samples.Application;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// The vocabulary the <c>docs/tutorials/specifications-with-code.md</c> tutorial builds up over the
/// <see cref="Account"/>: four sentence steps, a decision table and a set verification. Every region
/// here is included into that page, so a change to the shape of a step changes the tutorial with it.
/// </summary>
public class AccountSteps
{
    // #region account-steps
    private readonly Account _account = new();
    private bool _accepted;

    [Given("an account with a balance of {balance}")]
    internal void AnAccountWithABalanceOf(decimal balance) => _account.Deposit(balance);

    [When("{amount} is withdrawn")]
    internal void IsWithdrawn(decimal amount) => _accepted = _account.TryWithdraw(amount);

    [Then("the withdrawal is {outcome}")]
    internal void TheWithdrawalIs(string outcome)
        => SpecAssert.Check("outcome", _accepted ? "accepted" : "refused", outcome);

    [Then("the balance is {balance}")]
    internal void TheBalanceIs(decimal balance) => SpecAssert.Check("balance", _account.Balance, balance);
    // #endregion account-steps

    /// <summary>
    /// The overdraft boundary as a decision table: <c>balance</c> and <c>amount</c> bind to the row
    /// method's parameters, and the returned value is judged against the one column no parameter
    /// names, <c>outcome</c>.
    /// </summary>
    // #region withdrawals-decided-as
    [Then("withdrawals are decided as")]
    internal Task WithdrawalsAreDecidedAs(StepTable table) => TableRunner.Run(this, nameof(decide), table);

    private string decide(decimal balance, decimal amount)
    {
        var account = new Account();
        account.Deposit(balance);
        return account.TryWithdraw(amount) ? "accepted" : "refused";
    }
    // #endregion withdrawals-decided-as

    /// <summary>
    /// The account's history as a set verification. Order is part of the claim, and no key columns
    /// are named, so a row matches on every column the table writes.
    /// </summary>
    // #region transactions-are
    [Then("the transactions are")]
    internal void TheTransactionsAre(StepTable expected)
        => SetVerificationComparer.Verify(_account.Transactions, expected, ordered: true);
    // #endregion transactions-are
}
