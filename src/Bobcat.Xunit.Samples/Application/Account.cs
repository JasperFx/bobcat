namespace Bobcat.Xunit.Samples.Application;

/// <summary>
/// The system under test for the <c>docs/tutorials/specifications-with-code.md</c> tutorial: a bank
/// account with one rule, that a withdrawal may not overdraw it. Plain C#, no store, nothing hosted.
/// </summary>
// #region account
public record Transaction(string Kind, decimal Amount);

public class Account
{
    public decimal Balance { get; private set; }
    public List<Transaction> Transactions { get; } = new();

    public void Deposit(decimal amount)
    {
        Balance += amount;
        Transactions.Add(new Transaction("Deposit", amount));
    }

    public bool TryWithdraw(decimal amount)
    {
        if (amount > Balance) return false;

        Balance -= amount;
        Transactions.Add(new Transaction("Withdrawal", amount));
        return true;
    }
}
// #endregion account
