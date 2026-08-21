# Bank Account (Event Sourcing) — Critter Stack Sample

## Domain Reference

**Inspired by:** [andreschaffer/event-sourcing-cqrs-examples](https://github.com/andreschaffer/event-sourcing-cqrs-examples) (Java)

A minimalistic bank domain demonstrating event sourcing with Wolverine's store-agnostic aggregate handlers. Not a code port — built from scratch using the Java project's domain concepts.

**It runs on two event stores from the same handlers.** `Program.cs` is the only file that names a store: `EventStore=Marten` (the default — Postgres, `ConnectionStrings:Marten`) or `EventStore=Fisher` (an embedded SQLite file, `ConnectionStrings:Fisher`, no docker). Everything else is written against the vocabulary Wolverine and JasperFx.Events share across Marten, Polecat and Fisher — `[DeciderFunction]`, `[Entity]`, `Storage.StartStream()`, `IEventStoreOperations`, `IDocumentReadOperations`, and `Snapshot<T>` projections. See `docs/sample-wiring.md` footgun 14 for the swap table, and CLAUDE.md "Bobcat.CritterStack is store-agnostic" for why (issue #103).

## Domain

- **Client** — enroll, update profile (event-sourced)
- **Account** — open, deposit, withdraw (event-sourced, with balance guard)
- **Transaction History** — inline projection built from deposit/withdrawal events

## Patterns Demonstrated

### Decider Function Workflow (`[DeciderFunction]`)

Deposit and withdrawal operations use Wolverine's store-agnostic `[DeciderFunction]` attribute (`Wolverine.Persistence.EventSourcing`; `Wolverine.Marten`'s `[AggregateHandler]` derives from it). Wolverine loads the Account aggregate from the event stream of whichever store is registered, passes it to the handler, appends the returned event, and commits — all in one transaction.

```csharp
[WolverinePost("/api/accounts/{accountId}/deposits")]
[DeciderFunction]
public static (IResult, FundsDeposited) Post(DepositFunds command, Account account)
{
    var newBalance = account.Balance + command.Amount;
    return (Results.NoContent(), new FundsDeposited(command.AccountId, command.Amount, newBalance));
}
```

### Starting a stream without naming the store

`EnrollClient` and `OpenAccount` return a `Storage.StartStream<T>(id, events)` side effect beside their `Created<T>` response. The endpoint stays a pure function; Wolverine appends through the registered event store in the same transaction. (`IEventStoreOperations` is the injectable alternative when a handler needs more than a start or an append.)

### Validate Against Aggregate State

Withdrawal validates against the loaded aggregate's balance using a separate `Validate` method (Railway Programming pattern):

```csharp
public static ProblemDetails Validate(WithdrawFunds command, Account account)
{
    if (account.Balance < command.Amount)
        return new ProblemDetails { Detail = "Insufficient funds", Status = 400 };
    return WolverineContinue.NoProblems;
}
```

### Inline Snapshot Projections

Account, Client **and the `AccountTransactions` read model** use `SnapshotLifecycle.Inline` — the snapshot document is always up-to-date after each event append. `Snapshot<T>` is the one projection shape every store spells identically, which is why the transaction history is a self-aggregating document (`Create(AccountOpened)`, `Apply(IEvent<FundsDeposited>)`, …) rather than a `SingleStreamProjection<,>` subclass — that base class is Marten's, and Fisher and Polecat each have their own.

### Store-agnostic reads

The query endpoints take `IDocumentReadOperations` (documents: `LoadAsync<T>`, `Query<T>()` + `JasperFx.Events.Documents.ToListAsync`) and `IEventStoreOperations` (`AggregateStreamAsync<T>`), which Wolverine resolves to the current session on Marten, Polecat and Fisher alike.

### Entity Loading with Batch Query

`OpenAccount` uses `[Entity]` to load the Client by `ClientId` — verifying the client exists before opening an account.

## Running

On Marten (the default) it needs the samples' Postgres — `cd samples && docker compose up -d`. On Fisher it needs nothing.

```bash
dotnet run                                                  # Marten
EventStore=Fisher dotnet run                                # Fisher, bank_account.db next to appsettings.json

dotnet run --project Tests -- run                           # the Bobcat specs, on Marten
EventStore=Fisher dotnet run --project Tests -- run         # the same specs, on Fisher
```

Fisher builds its schema lazily, so the host calls `ApplyAllDatabaseChangesOnStartup()` — without it the first append on a fresh file fails with `no such table: fi_streams` (footgun 13).

**Polecat (SQL Server) is the documented manual path, not a wired one.** It would be a third `case` in `Program.cs` of the same shape — `AddPolecat(opts => { opts.Connection(...); opts.Projections.Snapshot<...>(SnapshotLifecycle.Inline); }).IntegrateWithWolverine()` from `WolverineFx.Polecat` — and nothing outside `Program.cs` would change, because Polecat registers `IEventStore`, implements the same `IDocumentReadOperations` / `IEventStoreOperations` contracts, and `Wolverine.Polecat`'s `[AggregateHandler]` derives from the same `[DeciderFunction]`. It is not in this repo's `Program.cs` because nothing here has run it against a SQL Server, and a branch nobody has run is footgun 4 waiting to happen.

Swagger UI at `/swagger`.

## API

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/clients` | Enroll a new client |
| PUT | `/api/clients/{clientId}` | Update client profile |
| GET | `/api/clients/{id}` | Get client |
| POST | `/api/accounts` | Open account for a client |
| GET | `/api/accounts/{id}` | Get account |
| GET | `/api/clients/{clientId}/accounts` | Get all accounts for a client |
| POST | `/api/accounts/{accountId}/deposits` | Deposit funds |
| POST | `/api/accounts/{accountId}/withdrawals` | Withdraw funds |
| GET | `/api/accounts/{accountId}/transactions` | Transaction history |
