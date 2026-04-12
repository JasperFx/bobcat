using System.Collections.Concurrent;

namespace PaymentsMonolith;

// Domain models
public record Account(int Id, string Name, string Email, decimal Balance);
public record Payment(int Id, int FromAccountId, int ToAccountId, decimal Amount, string Currency, string Status, string Reference);

// Request models
public record CreateAccountRequest(string Name, string Email, decimal InitialBalance);
public record MakePaymentRequest(int FromAccountId, int ToAccountId, decimal Amount, string Currency, string Reference);

public static class PaymentStore
{
    private static readonly ConcurrentDictionary<int, Account> _accounts = new();
    private static readonly ConcurrentDictionary<int, Payment> _payments = new();
    private static int _nextAccountId = 1;
    private static int _nextPaymentId = 1;

    public static void Reset()
    {
        _accounts.Clear();
        _payments.Clear();
        _nextAccountId = 1;
        _nextPaymentId = 1;
    }

    // Accounts
    public static Account CreateAccount(CreateAccountRequest req)
    {
        var id = _nextAccountId++;
        var account = new Account(id, req.Name, req.Email, req.InitialBalance);
        _accounts[id] = account;
        return account;
    }

    public static Account? GetAccount(int id) => _accounts.TryGetValue(id, out var a) ? a : null;

    // Payments
    public static (Payment? Payment, string? Error) MakePayment(MakePaymentRequest req)
    {
        if (!_accounts.TryGetValue(req.FromAccountId, out var sender))
            return (null, "From account not found");
        if (!_accounts.TryGetValue(req.ToAccountId, out var receiver))
            return (null, "To account not found");
        if (sender.Balance < req.Amount)
            return (null, "Insufficient funds");

        // Debit sender, credit receiver
        _accounts[req.FromAccountId] = sender with { Balance = sender.Balance - req.Amount };
        _accounts[req.ToAccountId] = receiver with { Balance = receiver.Balance + req.Amount };

        var id = _nextPaymentId++;
        var payment = new Payment(id, req.FromAccountId, req.ToAccountId, req.Amount, req.Currency, "completed", req.Reference);
        _payments[id] = payment;
        return (payment, null);
    }

    public static Payment? GetPayment(int id) => _payments.TryGetValue(id, out var p) ? p : null;

    public static IEnumerable<Payment> GetPaymentsForAccount(int accountId) =>
        _payments.Values
            .Where(p => p.FromAccountId == accountId || p.ToAccountId == accountId)
            .OrderBy(p => p.Id);

    public static IEnumerable<Payment> GetPaymentsByStatus(string status) =>
        _payments.Values.Where(p => p.Status == status).OrderBy(p => p.Id);

    public static Payment? RefundPayment(int id)
    {
        if (!_payments.TryGetValue(id, out var payment)) return null;
        if (payment.Status != "completed") return null;

        // Reverse the transfer
        if (_accounts.TryGetValue(payment.FromAccountId, out var origSender) &&
            _accounts.TryGetValue(payment.ToAccountId, out var origReceiver))
        {
            _accounts[payment.FromAccountId] = origSender with { Balance = origSender.Balance + payment.Amount };
            _accounts[payment.ToAccountId] = origReceiver with { Balance = origReceiver.Balance - payment.Amount };
        }

        var refunded = payment with { Status = "refunded" };
        _payments[id] = refunded;
        return refunded;
    }
}
