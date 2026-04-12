using System.Collections.Concurrent;

namespace BankAccountES;

public record AccountEvent(string Type, decimal Amount, DateTimeOffset Timestamp);

public record AccountState(string Id, string Owner, decimal Balance, List<AccountEvent> Events);

public record OpenAccountRequest(string Owner, decimal InitialDeposit);
public record DepositRequest(decimal Amount);
public record WithdrawRequest(decimal Amount);

public record AccountView(string Id, string Owner, decimal Balance);
public record DepositView(string Id, string Owner, decimal Balance, decimal Amount);
public record TransactionView(string Type, decimal Amount, DateTimeOffset Timestamp);

public static class AccountStore
{
    private static readonly ConcurrentDictionary<string, AccountState> _accounts = new();
    private static int _nextId = 1;

    public static void Reset()
    {
        _accounts.Clear();
        _nextId = 1;
    }

    public static AccountView Open(OpenAccountRequest req)
    {
        var id = $"ACC{_nextId++:D4}";
        var events = new List<AccountEvent>
        {
            new("Opened", req.InitialDeposit, DateTimeOffset.UtcNow)
        };
        var state = new AccountState(id, req.Owner, req.InitialDeposit, events);
        _accounts[id] = state;
        return new AccountView(state.Id, state.Owner, state.Balance);
    }

    public static AccountState? GetById(string id) =>
        _accounts.TryGetValue(id, out var a) ? a : null;

    public static (AccountState state, decimal amount)? Deposit(string id, DepositRequest req)
    {
        if (!_accounts.TryGetValue(id, out var existing)) return null;
        var evt = new AccountEvent("Deposit", req.Amount, DateTimeOffset.UtcNow);
        var updated = existing with
        {
            Balance = existing.Balance + req.Amount,
            Events = [.. existing.Events, evt]
        };
        _accounts[id] = updated;
        return (updated, req.Amount);
    }

    public static (AccountState state, bool success)? Withdraw(string id, WithdrawRequest req)
    {
        if (!_accounts.TryGetValue(id, out var existing)) return null;
        if (existing.Balance < req.Amount) return (existing, false);
        var evt = new AccountEvent("Withdrawal", req.Amount, DateTimeOffset.UtcNow);
        var updated = existing with
        {
            Balance = existing.Balance - req.Amount,
            Events = [.. existing.Events, evt]
        };
        _accounts[id] = updated;
        return (updated, true);
    }
}

public static class AppBootstrap
{
    public static void MapRoutes(WebApplication app)
    {
        app.MapPost("/api/accounts", (OpenAccountRequest req) =>
        {
            var view = AccountStore.Open(req);
            return Results.Created($"/api/accounts/{view.Id}", view);
        });

        app.MapGet("/api/accounts/{id}", (string id) =>
        {
            var account = AccountStore.GetById(id);
            if (account is null) return Results.NotFound();
            return Results.Ok(new AccountView(account.Id, account.Owner, account.Balance));
        });

        app.MapPost("/api/accounts/{id}/deposit", (string id, DepositRequest req) =>
        {
            var result = AccountStore.Deposit(id, req);
            if (result is null) return Results.NotFound();
            var (state, amount) = result.Value;
            return Results.Ok(new DepositView(state.Id, state.Owner, state.Balance, amount));
        });

        app.MapPost("/api/accounts/{id}/withdraw", (string id, WithdrawRequest req) =>
        {
            var result = AccountStore.Withdraw(id, req);
            if (result is null) return Results.NotFound();
            var (state, success) = result.Value;
            if (!success) return Results.BadRequest(new { error = "Insufficient funds", balance = state.Balance });
            return Results.Ok(new AccountView(state.Id, state.Owner, state.Balance));
        });

        app.MapGet("/api/accounts/{id}/transactions", (string id) =>
        {
            var account = AccountStore.GetById(id);
            if (account is null) return Results.NotFound();
            var transactions = account.Events.Select(e => new TransactionView(e.Type, e.Amount, e.Timestamp));
            return Results.Ok(transactions);
        });
    }
}
