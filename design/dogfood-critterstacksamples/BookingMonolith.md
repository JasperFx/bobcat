# BookingMonolith
- Tests before: 16 (1 file `Tests/BookingMonolithTests.cs`, xUnit v2 2.9.2 + VSTest + Alba 8.5.3 + Shouldly; every test arranged through HTTP POSTs to other modules' endpoints)
- Specs after: 16 in 4 feature classes (Identity 3, Passenger 2, Flight 5, Booking 6) on `WolverineHttpSpec`: **16 passing / 0 failing / 0 skipped**, green on first run.
- Not compared against the in-repo store-only port at `samples/BookingMonolith`.

## Adoption steps (what it took)
Same recipe as BankAccountES (csproj, global.json, TestSupport.cs, AssemblyAttributes.cs), copied over in ~2 minutes:
- App WolverineFx.* 6.30.0 -> 6.51.0, added explicit `Marten 9.50.3` (transitive would do), app stays net9.0, `public partial class Program;`.
- Test project: same package set (Bobcat* 0.60.4, WolverineFx.Bobcat(.Http) 6.51.0-alpha.1, Alba 9.0.0-rc.2, xunit.v3 4.0.2, MTP runner), net10.0, `EmitCompilerGeneratedFiles`.
- `[assembly: EventModelName("BookingMonolith")]` (= ServiceName), Parallelization None.
- Connection string through `ConnectionStrings__Marten` env var set in the fixture (this app ends in `app.RunAsync()`, not `RunJasperFxCommands`, so `UseSetting` would also have worked here).
- The app's four durable local queues (`UseDurableInbox`) and cascading integration events with NO handler on three of them (PassengerCreated, FlightCreated, BookingCreated) caused no trouble for the tracked session.

## Diagnostics, crashes, surprising renders
- `CS0411: The type arguments for method 'CritterStackSpec.GivenReadModel<T>(object)' cannot be inferred` — the signature takes `object`, so `GivenReadModel(new Flight{…})` never compiles; must write `GivenReadModel<Flight>(new Flight{…})`. Low, ergonomics (Bobcat).
- No BOBCAT diagnostics, no generated-code errors, no crashes.
- Surprising renders (low):
  - A command whose arguments do not fit on one line renders as a table **with an empty row #1**:
    ```
    ✓ When  CreateBooking is posted to "/api/bookings" (98ms)
    │ # │ message       │ values                                             │
    │ 1 │               │                                                    │
    │ 2 │ CreateBooking │ PassengerId: 00000000-…, FlightId: 00000000-…, …   │
    ```
    (same for every `CreateFlight` POST).
  - `GivenReadModel<Flight>(new Flight { Id = flightId, FlightNumber = "FL300", … })` renders as `Given the Flight read model is Flight` — none of the object initializer's values appear, so the arrange is unreadable.
  - Inline `Guid.NewGuid()` args render as their parameter name minus `Id`: `AircraftId: Aircraft, DepartureAirportId: DepartureAirport`, `PassengerId: Passenger`.
  - Lists again: `When List\`1 is read from "/api/flights"`, `Then the Int32 matches ✓ Int32: Int32 ✓ value: 1`.
  - `DateTime` args render as `2026-06-01T08:00:00.0000000`.
- ~950 lines of host logging precede the specification tree when the exe runs directly (Wolverine/Marten info logs); not Bobcat's, but it buries the render.

## Vocabulary used / missing
- Used: `WhenPosted(cmd)`, `WhenPosted<T>(cmd)`, `WhenReading<T>(route, values)`, `GivenReadModel<T>`, `GivenEvents`, `ThenResponseIs`, `ThenRefusedWith("Email")` (substring of the FluentValidation ProblemDetails), `ThenNotFound`, `ThenNoEvents`, `ThenStreamIsStartedWithEvents<T>`, `ThenAggregate<T>`, `ThenDocument<T>`, `ThenNoReadModel<T>()`, `ThenMessageSent(Specify<T>()…)`, `ThenNoMessageSent<T>`, `ThenMatches`.
- Better than the original: arrange is now direct (documents / events) instead of POSTing to three other modules; the integration events (UserCreated, PassengerCreated, FlightCreated, BookingCreated) are now asserted, and the cross-module reaction (UserCreated -> Passenger document) is asserted with `ThenDocument<Passenger>` after the act — the tracked session waited for the durable local queue.
- Missing: (1) a document-side `GivenContext` — a Command slice whose handler looks documents up with `[Entity]` (CreateBooking loads Passenger + Flight) can only arrange them with `GivenReadModel`, which stamps them as the slice's *read models* (see below); (2) a count/collection step (see BankAccountES; `Expect.*` not in 0.60.4); (3) no way to say "a document was stored" on the model: `ThenDocument` is resolved-types only, so RegisterUser / CreatePassenger / CreateFlight slices show a command and a published message but nothing they write.
- No raw Alba/Shouldly fallback was needed.

## Event Model and manifest
- Manifest: all 16 identities as expected (`Booking/create booking with valid passenger and flight`, `Flight/get flight by id returns 404 for missing`, …).
- Model name `BookingMonolith`. Command slices CreateBooking / CreateFlight / RegisterUser / CreatePassenger: command, Http trigger, `PublishedMessages` (BookingCreated / FlightCreated / UserCreated / PassengerCreated) right; CreateBooking `Events = BookingCreatedDomainEvent`, `AggregateTypes = BookingRecord` right. As in BankAccountES, by-type `WhenPosted` leaves **no TriggerOrigin** (no route/verb).
- **Wrong: CreateBooking has `ReadModelTypes = [Passenger, Flight]`** — the documents arranged with `GivenReadModel` for the `[Entity]` lookups. They are lookups (`ReadsFrom`), not read models a command slice produces.
- **Wrong: `GET /api/flights` is `Pattern = View`** with `ReadModelTypes = Flight`, no trigger: `WhenReading<List<Flight>>` is not recognised as a read act (collection T), so the slice is "only arranges/asserts a read model" = View. `GET /api/bookings` (`WhenReading<List<BookingRecord>>`) has no Pattern/trigger and `AggregateTypes = BookingRecord` from the `GivenEvents`. Both should be Query slices. Single-object reads (`GET /api/flights/{id}`, `GET /api/bookings/{id}`) are correct Query slices with route, GET and `ReadsFrom`.
- The UserCreated -> Passenger handler (an automation) has no slice; it would need its own spec with `WhenReceived(new UserCreated(…))`.

## ai-skills gaps
- No guidance for arranging *documents* a command looks up (`[Entity]` on a document): `GivenContext` is event-stream only; the skill does not say what `GivenReadModel` does to a Command slice on the model.
- `GivenReadModel<T>(document)` is listed as if `T` were inferred; it is not (parameter is `object`).
- Everything in BankAccountES's ai-skills list applies (headline example, unreleased `Expect.*`, by-type acts lose route).

## Candidate issues
- Bobcat (generator): `WhenReading<List<T>>` / `IReadOnlyList<T>` -> read act with element type; today a list GET becomes a **View** when arranged with `GivenReadModel` (worse than "no pattern").
- Bobcat (generator): documents arranged with `GivenReadModel` on a slice whose act is a command should stamp `ReadsFrom`, not `ReadModelTypes` — or add a `GivenContextDocument<T>`/`GivenDocument<T>` with `SliceRole.Context`.
- Bobcat: `GivenReadModel<T>(T document)` (generic parameter) so `T` is inferred.
- Bobcat (rendering): empty first row in the multi-line command table; `GivenReadModel` should render the document's values; `Guid.NewGuid()` named as `Aircraft`/`Passenger`.
- Bobcat (generator) / WolverineFx.Bobcat: by-type `WhenPosted(cmd)` should resolve the route from the endpoint accepting the type (repeat of BankAccountES).
