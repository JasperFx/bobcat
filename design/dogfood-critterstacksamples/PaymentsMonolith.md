# PaymentsMonolith
- Tests before: 10 (1 file `PaymentsTests.cs`, xUnit v2 + VSTest + Alba 8.5.3 + Shouldly)
- Specs after: 11 in 4 feature classes (Customers 2, Payments 2, Users 3, Wallets 4) on `WolverineHttpSpec`: **11 passing / 0 failing / 0 skipped**.
- Document-only app (Marten documents for Wallet/Customer/Deposit/User, no event streams), cascading integration messages.
- (Port begun by the first agent run, finished and checked by hand in the second.)

## Adoption steps (what it took)
Same recipe as BankAccountES: WolverineFx.* 6.30.0 -> 6.51.0 (app stays net9.0); test csproj rewritten (xunit.v3 4.0.2, MTP runner,
Bobcat* 0.60.4, WolverineFx.Bobcat.Http 6.51.0-alpha.1, **Alba forced to 9.0.0-rc.2**), `global.json` test runner, an
`AppFixture`/collection/abstract `InflowSpec : WolverineHttpSpec` with `ResetAsync()`, `[assembly: EventModelName("Inflow")]`,
serial parallelization. The app ends in `app.RunAsync()` (no `RunJasperFxCommands`), so Alba `UseSetting` for the connection
string worked here without the "Invalid usage" noise other samples hit.

## Diagnostics, crashes, surprising renders
- No BOBCAT diagnostics, no generated-code errors, no crashes.
- `GivenReadModel<Wallet>(wallet)` renders `Given the Wallet read model is Wallet` — none of the document's values (same as BookingMonolith).
- Inline `Guid.NewGuid()` / ids taken from an arranged object render as a parameter-derived name: `TransferFunds(FromWalletId: Wallet, ToWalletId: Wallet2, …)`, `CompleteCustomer(CustomerId: Customer, …)`.
- `ThenRefusedWith(...)` against a ProblemDetails renders a table **with an empty row 1** (the same defect as the multi-line command table):
  ```
  ✓ And   refused with ProblemDetails(Detail: Insufficient funds)
  │ # │ Detail             │ Status │
  │ 1 │                    │   OK   │
  │ 2 │ Insufficient funds │   OK   │
  ```
- A document with its own `Status` member gets a verdict column titled `Verdict` instead of `Status` (`│ Status │ Verdict │`). Sensible, but a reader of two adjacent tables sees two titles for the same column.
- Every `ThenDocument` adds `✓ And the Wallet read model has caught up` for documents no projection builds (noise, as in CqrsMinimalApi).

## Vocabulary used / missing
- Used: `GivenReadModel<T>`, `WhenPosted(cmd)`, `WhenPosted<T>(cmd)`, `WhenReading<List<T>>(route)`, `ThenResponseIs`, `ThenNotFound`,
  `ThenRefusedWith`, `ThenDocument<T>(id, Specify<T>(x => x.Balance.ShouldBe(…), …))` (member checks), `ThenNoReadModel<T>`,
  `ThenMessageSent(Specify<T>()…)`, `ThenNoMessageSent<T>`, `ThenMatches`.
- Missing (had to write on the base class):
  - **A document created by a cascaded handler under an id the spec cannot know** (CompleteCustomer -> WalletCreated -> a Wallet document
    with a fresh id). Wrote `AllDocuments<T>()` with a raw `IDocumentStore.QuerySession()` plus a `[Then("exactly one item is in the list")]`
    count step, because there is no `ThenSingleDocument<T>(predicate)`-by-query form that fits and no count/collection step in 0.60.4.
  - Same `GivenReadModel<T>(object)` inference gap (must restate `T`).

## Event Model and manifest
- Manifest: all 11 identities as expected (`Customers/an unknown customer cannot be completed`, …).
- Model `Inflow`. Command slices CompleteCustomer / CreateDeposit / RegisterUser / AddFunds / TransferFunds: command, Http trigger and
  `PublishedMessages` right; **no route** on any (all by-type `WhenPosted`).
- **Wrong: `GET /api/wallets` is `Pattern = View`** — `WhenReading<List<Wallet>>` is not a read act (the trend from BankAccountES/CqrsMinimalApi/BookingMonolith).
- Documents arranged with `GivenReadModel` before a command act are stamped as that command slice's read models (BookingMonolith trend).

## ai-skills gaps
- Nothing beyond the BankAccountES/CqrsMinimalApi list. No guidance for asserting a document whose id only the system knows.

## Candidate issues
- Bobcat: list `WhenReading` read act (dup of the trend). Bobcat (rendering): empty row 1 in the refusal table (same root as the command table).
- WolverineFx.Bobcat: a "the one document of type T matching…" step for documents created downstream under a system-chosen id.
