# LibraryQuickstart
- Tests before: 4 (1 file `LibraryTests.cs` + `AppFixture.cs`, xUnit v2 + VSTest + Alba 8.5.3 + Shouldly + **Testcontainers.PostgreSql**)
- Specs after: 6 on `WolverineHttpSpec` (the 4 originals, the long "whole loop" test split into add / borrow / return / list): **6 passing / 0 failing / 0 skipped**.
- App: event-sourced (`Book` and `BorrowedBook` self-aggregating snapshots, inline), `[Aggregate]`/`[ReadAggregate]` endpoints,
  `MartenOps.StartStream`, fast event forwarding to a Wolverine handler (`IEvent<BookReturned>`), already net10.0, ends in `RunJasperFxCommands(args)`.

## Adoption steps (what it took)
- WolverineFx.* 6.34.0 -> 6.51.0, Marten 9.32.1 -> 9.50.3 in Api, Library, Quickstart.
- Same test-project recipe as the others; keep `AssemblyName=Tests` (`InternalsVisibleTo Include="Tests"`).
- **Testcontainers dropped** for this run (the brief forbids starting containers); the fixture points at 5445 through
  `ConnectionStrings__Marten`, because the app ends in `RunJasperFxCommands(args)` and Alba's `UseSetting` would arrive as an unknown
  argument (the BankAccountES/CqrsMinimalApi trap). Nothing in Bobcat stops a suite keeping Testcontainers — `WolverineHttpSpec` only wants
  the `IAlbaHost` — and the skill could say so, since a Testcontainers fixture is the most common shape in the wild.
- `JasperFxEnvironment.AutoStartHost = true` needed in the fixture (`RunJasperFxCommands`), as in BankAccountES.

## Diagnostics, crashes, surprising renders
- `warning CS4014` on `ThenStreamIsStartedWithEvents<Book>(…)`: it returns a Task while `ThenEvents` is synchronous. Forgetting the
  `await` compiles with only a warning, so a stream-start assertion can be left unawaited (and race the next step). Mixed sync/async Then
  steps on one scenario are an easy trap; the skill does not flag which are which.
- `ThenMessageSent<IEvent<BookReturned>>()` renders `And IEvent\`1 is sent`; `WhenReading<IReadOnlyList<BorrowedBook>>` renders
  `When IReadOnlyList\`1 is read from "/books/on-loan"`; an event with no members renders `│ BookReturned │ BookReturned │` (type name in the values column).
- The return endpoint takes no body, so the act is `WhenPosted(new { }, route)` → `When <>f__AnonymousType0 is posted to …`.
- No BOBCAT diagnostics.

## Vocabulary used / missing
- Used: `GivenEventsOn<T>(id, …)`, `WhenPosted<T>(cmd)`, `WhenPosted<T>(cmd, route)`, `WhenPosted(new { }, route)`, `WhenReading<T>(route)`,
  `ThenResponseIs`, `ThenNotFound`, `ThenRefusedWith`, `ThenEvents`, `ThenNoEvents`, `ThenStreamIsStartedWithEvents<T>`,
  `ThenReadModel<T>(id, …)`, `ThenNoReadModel<T>(id)`, `ThenMessageSent<T>()`, `ThenMatches`.
- Pleasant: event forwarding to the `IEvent<BookReturned>` handler is visible to the tracked session, so "the reactor heard it" is one step.
- Missing: a body-less POST act (`WhenPosted(route)` for an endpoint that takes only route values — the return/approve/cancel shape is
  everywhere in Wolverine.HTTP); asserting on an `IEvent<T>` envelope's metadata (the original checked `StreamId`) needs a raw tracked session.

## Event Model and manifest
- Manifest: all 6 identities as expected.
- `AddBook`: Command, Http, `AggregateTypes = Book`, no route (by-type act). `BorrowBook`: Command, POST `/books/{bookId}/borrow`, three specs. Correct.
- **Wrong: the return command and the on-loan query merged into one bogus slice.** The return spec's act is an anonymous type (no Command
  role) and the list read is `IReadOnlyList<BorrowedBook>` (no read act), so neither has a slice of its own; both fall onto the feature-named
  slice `Library`, which — having only arranged events and read models — becomes **Pattern View, AggregateTypes Book, ConsumedEvents
  BookAdded/BookBorrowed**. On the canvas that is a view called "Library" that exists in no code, and the `ReturnBook` command slice is missing.

## ai-skills gaps
- Testcontainers fixtures: say they are fine. Say which Then steps are async.
- No body-less act; say what to do for a route-only POST (and that it costs the slice).

## Candidate issues
- WolverineFx.Bobcat: body-less `WhenPosted(route, …)` / `WhenDeleted(route, …)` acts carrying `[EventModelTrigger(Http, Route=…, HttpMethod=…)]`, so the slice still gets its route and verb.
- Bobcat (generator): role-less specs falling onto a feature-named slice merge unrelated acts into one slice whose pattern is then derived from the leftovers (View). Either keep them off the model or give each act its own slice.
- Bobcat (rendering): generic type names (`IEvent\`1`, `IReadOnlyList\`1`) in step sentences.
