# OutboxDemo
- Tests before: 4 (1 file `RegistrationTests.cs`, xUnit v2 + VSTest + Alba 8.5.3 + Shouldly, per-class host + `CleanAllMartenDataAsync`)
- Specs after: 4 on `WolverineHttpSpec` (3 ported + 1 new for the saga's `RegistrationValidated` handler): **4 passing / 0 failing / 0 skipped**.
- App: Wolverine.HTTP endpoint that cascades a Marten-persisted **saga** (`Registration : Saga`) plus a message through the Marten
  outbox; the saga cascades two more messages. References `WolverineFx.RabbitMQ` but configures no Rabbit endpoint, so
  `DisableAllExternalWolverineTransports` was enough — no broker needed.

## Adoption steps (what it took)
Same recipe as BankAccountES/PaymentsMonolith (WolverineFx.* 6.30.0 -> 6.51.0, app net9.0; xunit.v3 + MTP; Bobcat* 0.60.4,
WolverineFx.Bobcat.Http 6.51.0-alpha.1, Alba 9.0.0-rc.2; `<Using Include="Xunit" />`; global.json; fixture + collection + abstract
`OutboxSpec : WolverineHttpSpec` with `ResetAsync()`; `EventModelName("OutboxDemo")`, serial). App ends in `app.RunAsync()`, so
`UseSetting` for the connection string was fine. One self-inflicted trip worth recording: rewriting the csproj from the skill's
template drops `<AssemblyName>OutboxDemo.Tests</AssemblyName>`, and the app's `InternalsVisibleTo Include="OutboxDemo.Tests"` then
gives `CS0122: 'Program' is inaccessible due to its protection level` — the template should say "keep your AssemblyName".

## Diagnostics, crashes, surprising renders
- **`ThenSingleDocument<Registration>()` refuses on a document-only app** (medium):
  `Bobcat.Engine.SpecCriticalException : ThenSingleDocument<Registration>() reads the stream the scenario is about, but the act started
  none and the givens arranged none. Name it: ThenReadModel<Registration>(id, ...).` — but the saga's id is minted inside the endpoint
  (`Guid.NewGuid()`), so there is no id to name. The step is stream-scoped even under the name "document". Worked around with a raw
  `QuerySession().Query<T>()` helper (same gap as PaymentsMonolith's downstream Wallet).
- The `SpecAssert.Check("documents", count, 1)` in that helper, called from the test body (no step open), is silent on pass — the
  count assertion does not appear in the tree at all.
- The tracked-session envelope table on failure wraps every cell at ~10 characters (`Registratio / nSubmitted`, `local://ou / tboxdemo.r / …`)
  — it is the right evidence, but unreadable at the default width; message type names and destinations deserve the width.
- `Given the Registration read model is Registration` (no values), `RegistrationValidated(RegistrationId: Registration)` (id named after its source) — as elsewhere.
- No BOBCAT diagnostics.

## Vocabulary used / missing
- Used: `GivenReadModel<T>`, `WhenPosted(cmd)`, `WhenReceived(msg)`, `ThenResponseIs`, `ThenRefusedWith` (matched ProblemDetails.Detail with status 409),
  `ThenMessageSent(Specify<T>()…)`, `ThenNoMessageSent<T>`, `ThenDocument<T>(id, …)`, `ThenMatches`.
- Pleasant: the tracked session waited for the whole cascade (endpoint -> outbox -> saga handler -> two more messages), so the saga's
  `CurrentState = "Submitted"` and the two downstream messages assert with no waiting code.
- Missing: no saga vocabulary (`GivenSaga<T>`, `ThenSaga<T>(id, …)`, `ThenSagaCompleted<T>`) — sagas are arranged and read as "read models";
  no "the only document of type T" step for a system-minted id.

## Event Model and manifest
- Manifest: all 4 identities as expected (`Registration/submit registration`, …).
- `SubmitRegistration`: Command, Http, **no route** (by-type `WhenPosted`, the cross-sample trend). `PublishedMessages` =
  RegistrationSubmitted **and SendRegistrationEmail, AddEventAttendee** — the last two are published by the *saga's* handler downstream,
  not by this slice's endpoint: every message the tracked session saw is stamped on the act's slice, so a cascade chain collapses into
  one slice on the canvas.
- The saga is stamped as the slice's **read model** (`Registration`) on both slices — a saga is state the handler decides against
  (closer to an aggregate), not a read model.
- `RegistrationValidated` (a `WhenReceived` message act): Pattern Command, **no TriggerKind** — a received message could be stamped
  `TriggerKind.Message`; Automation is left null as documented.

## ai-skills gaps
- csproj template: say "keep the project's AssemblyName/RootNamespace" (InternalsVisibleTo), plus the `<Using Include="Xunit" />` gap.
- No word on sagas, or on document-only apps where every `ThenSingle*` step needs a stream.

## Candidate issues
- Bobcat: `ThenSingleDocument<T>()` / `ThenSingleReadModel<T>()` should work (query the store for the one T) when the scenario has no stream, instead of refusing.
- Bobcat/WolverineFx.Bobcat (generator): only messages the act's own handler/endpoint published belong on its slice; downstream cascades from other handlers should not be stamped there (or stamp them on their handler's slice).
- WolverineFx.Bobcat: saga vocabulary (`ThenSaga<T>`), and stamp a saga as the slice's state rather than its read model.
- Bobcat (rendering): tracked-session envelope table column widths.
