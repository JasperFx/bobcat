# ShipmentTracking
- Tests before: 33 (7 files, xUnit v2 + VSTest, Alba 8.5.3 + Shouldly; real SQL Server 2025 via Polecat + real RabbitMQ)
- Specs after: 33, **build-only**: 0 passed / 33 failed at run time, all with `Collection fixture type 'Tests.AppFixture' threw in InitializeAsync ---- Microsoft.Data.SqlClient.SqlException : A network-related … (Connection refused)` (no SQL Server on 1433, no RabbitMQ on 5672 on this machine). Manifest and Event Model checked.

## Adoption steps (what it took)
- App: net9.0 -> net10.0; WolverineFx, .Http, .RabbitMQ, .RuntimeCompilation, .Polecat 6.32.0 -> 6.51.0; Polecat 5.22.0 -> 5.42.0 (what WolverineFx.Polecat 6.51.0 needs). `Wolverine.CritterWatch` left at 1.0.1 (built against WolverineFx 6.29.1 / JasperFx 2.52.1; resolves and compiles against 6.51, binary compatibility unverified because the host could not start). AppHost (net9.0, references the app) not touched.
- Tests.csproj: same template as ProjectManagement (xunit.v3 4.0.2, MTP, Bobcat.Xunit/Generators 0.60.4, WolverineFx.Bobcat.Http 6.51.0-alpha.1, Shouldly), `global.json` with the MTP runner, `EmitCompilerGeneratedFiles`.
- `GlobalUsings.cs` gained `Bobcat`, `Bobcat.Xunit`, `Wolverine.Bobcat`, `Wolverine.Bobcat.Http`, `using static Bobcat.Specifications`.
- `AssemblyAttributes.cs`: `[assembly: EventModelName("ShipmentTracking")]` (= `opts.ServiceName`), `[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]` (every spec resets the one database).
- Fixture kept almost verbatim (database creation over ADO.NET, `DisableCritterWatch()`, `ListenToRabbitQueue("shipment-operations")`, `InstantCarrierLabelClient`, solo mode). `IAsyncLifetime` -> `ValueTask` (xUnit v3).
- **Alba 9.0.0-rc.2 arrives transitively through WolverineFx.Bobcat.Http and removes `ConfigurationOverride`**: `error CS0103: The name 'ConfigurationOverride' does not exist`. And `UseSetting("ConnectionStrings:…", "Server=…;Database=…")` is rejected by `RunJasperFxCommands` (see ProjectManagement). Workaround: `Environment.SetEnvironmentVariable("ConnectionStrings__Shipments", …)` in the fixture.
- Base: `ShipmentSpec : WolverineHttpSpec, IAsyncLifetime` with `ResetAsync()` per spec and `Timeout = 30.Seconds()` (the original `Track()` used 30 s; WolverineSpec's default is 5 s).
- **The usual `DisableAllExternalWolverineTransports()` is wrong for this app** (the original fixture documents why: stubbing turns `ToRabbitQueue(...)` routes into `NullSender`s, so `BookShipment` is "Sent" and never handled — a green test over nothing). So `WolverineSpec` here needs the real broker; there is no in-memory way to run these specs. The skill's fixture template recommends `DisableAllExternalWolverineTransports()` unconditionally.

## Diagnostics, crashes, surprising renders
1. `TheResponse<T>()` returns `Task<T?>`: `error CS1061: 'Task<ShipmentAccepted?>' does not contain a definition for 'ShipmentId'`. The skill lists it under "Reading what happened (not steps)" next to synchronous members, nothing says async. Low.
2. `services.GetRequiredService` inside `WhenActing(…, services => …)` needs `using Microsoft.Extensions.DependencyInjection;` — trivial, but every WhenActing example in the skill uses it without the using.
3. **`BOBCAT_SPEC_PREVIEW=1 --list-tests` previews 4 of 33 specs, and only my own `[Then]` helper**:
   ```
   a duplicate delivery notification does not blow up the saga
       ○ Then  the act completed with no exceptions
   …
   4 specification(s) previewed
   ```
   The `WolverineSpec`/`WolverineHttpSpec` vocabulary (`WhenReceived`, `ThenDocument`, `ThenMessageSent`, …) is invisible to the preview, so for a suite written in the vocabulary the preview is nearly empty. The skill sells the preview as "previews every spec without running anything". Medium.
4. Host start failure (no SQL Server) renders as a plain xUnit fixture error on each of the 33 tests, no Bobcat output at all — matches the skill's "every test … Collection fixture type threw" row. Fine.

## Vocabulary used / missing
- Used: `WhenPosted(request, route)` (incl. interpolated `$"/shipments/{id}/cancel"`), `WhenReceived`, `WhenReading<T>(route, values)`, `WhenActing`, `ThenResponseIs`, `ThenNotFound`, `TheResponse<T>`, `ThenMessageSent<T>(destination)`, `ThenMessageSent(Specify<T>()…)`, `ThenNoMessageSent<T>`, `ThenMessageScheduled<T>(5.Days())`, `ThenDocument<T>(id, Specify<T>()…)`, `ThenNoReadModel<T>(id)` (for "the saga document is gone"), `ThenRefusedWith<TException>()`, `ThenRefusedWith(text)`, `ThenMatches`.
- This app has **no event sourcing** (Polecat documents + a Wolverine saga), so most of the vocabulary (GivenEvents, ThenEvents, ThenAggregate) does not apply; the message + document steps covered most assertions.
- Gaps, with what I wrote instead:
  - **Executed messages** (`tracked.Executed.SingleMessage<GenerateLabel>()`): no step. `LastAct.Session!.Executed.SingleMessage<…>()` raw.
  - **"Published but no route" (`MessageEventType.NoRoutes`)**: no step; `ThenMessageSent` does not see it and `ThenNoMessageSent` passes over it. Raw `LastAct.Session!.AllRecordsInOrder()` helper.
  - **"The act completed with no exceptions"** (`tracked.Status == Completed`, `AllExceptions()` empty): no positive step (there are refusal steps, but nothing like `ThenTheActSucceeded()`). Wrote a `[Then]` helper with `SpecAssert.Check` on `LastAct.Session`.
  - **Playing scheduled messages** (`PlayScheduledMessagesAsync`): no act; the played session is not `LastAct`, so nothing after it is a step. Raw Shouldly on the returned session. (An act like `WhenScheduledMessagesArePlayed()` would fit the SLA-timeout specs exactly.)
  - **`WhenReceived` of the SLA timeout only reschedules it** (bobcat#674, fix moved to wolverine#5077): `DeliverySlaExpired` is a `TimeoutMessage(5.Days())`, so Wolverine puts it back on the schedule, the handler never runs, and `ThenNoMessageSent<EscalateLateShipment>()` goes green over nothing. Only the positive form failed, naming the reschedule. Wrote `WhenActing` + `IMessageBus.InvokeAsync` and a check over `LastAct.Session!.Executed`; the warning and that workaround were on Bobcat's old Wolverine integration page, now the Wolverine docs' [Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat).
  - **Response headers** (`Location`): no step; raw `LastResponse!.Context.Response.Headers.Location`.
  - **Arranging through the real handler**: the documents-only app has nothing to `GivenEvents` with; the original's `BookShipment()` helper became `GivenABookedShipment()` calling `WhenReceived` — which renders as a **When** step (and every spec now shows two or three "When"s). `GivenReadModel<Shipment>(doc)` exists but bypasses the saga, which several specs depend on. A `Given…Received(message)` arrange form would read correctly.
  - `ThenDocument<T>(id)` returning the document is handy, but reading *arranged* state before the act has no step; wrote a private `TheDocumentAsync` over `IDocumentStore.QuerySession()`.
  - Optimistic-concurrency spec: pure store test; `WhenActing` + `ThenRefusedWith<ConcurrencyException>()` + `ThenDocument` fit well.

## Event Model and manifest
- Manifest: all 33 identities present (`Booking a shipment/the endpoint answers 202 with a location header`, …), `framework: xunit`, `lane: projected`. As expected.
- `BobcatEventModelSource.g.cs` (model `ShipmentTracking`, 12 slices). Right: `BookShipmentRequest` (POST /shipments, publishes BookShipment), `CancelShipmentRequest` (route resolved from the interpolated string to `/shipments/{id}/cancel`), `BookShipment` (publishes ShipmentBooked, GenerateLabel, DeliverySlaExpired), `GET /shipments/{id}` (Query, ReadsFrom Shipment, GET trigger).
- Wrong / surprising:
  - **`GET /shipments` slice is empty**: no Pattern, no trigger, no ReadsFrom, although the spec calls `WhenReading<List<Shipment>>("/shipments")` with a constant route. A collection read model (`List<Shipment>`) apparently stamps no `ReadModel` role, and without it the GET is not a read act, so even the trigger is dropped. Should unwrap the element type, or at least keep the HTTP GET trigger. Medium.
  - **`DeliverySlaExpired` slice has `CommandType = BookShipment`**. Three specs bind to `typeof(DeliverySlaExpired)`; in two of them the last *vocabulary* act is `WhenReceived(new BookShipment…)` / `RecordCarrierScan` because the real act (`PlayScheduledMessagesAsync`) is outside the vocabulary. No diagnostic says "the slice is named for X but its act sends Y", and the merge across specs silently took the first. Medium (my binding was arguably wrong, but nothing told me).
  - **An empty slice `Optimistic concurrency`** (no pattern, no command, nothing) is emitted for a `[BobcatSpec]` with no type whose only roles are `ThenDocument` (`Document` -> resolved types only). Noise on the canvas. Low.
  - Documents (`Shipment`, `ShipmentDeliverySaga`) never appear on the model at all (by design — `Document` is resolved types only), so for a document-store app the model is command slices with messages and no state. Worth a sentence in the skill.
  - HTTP slices are named after the request DTOs (`BookShipmentRequest`), the binding I chose; whether that merges with Wolverine's derived HTTP chain names is unverified (no host).

## ai-skills gaps
- Fixture template's `services.DisableAllExternalWolverineTransports()` is presented as the default; it silently breaks apps whose explicit `ToRabbitQueue` routing is the point (this sample's own fixture explains it). Needs a caveat.
- "Arrange with events, never with the handler under test" has no answer for a documents/saga app; say what to do (GivenReadModel, or an earlier act).
- `TheResponse<T>()` is async; `WhenActing` examples need `Microsoft.Extensions.DependencyInjection`.
- No mention that WolverineFx.Bobcat.Http drags in **Alba 9 rc** (breaking: `ConfigurationOverride` gone).
- Preview claim ("previews every spec") is not true for vocabulary-only specs.
- Missing steps worth listing as gaps or adding: Executed, NoRoutes, act-succeeded, PlayScheduledMessages.

## Candidate issues
- WolverineFx.Bobcat (wolverine): `ThenMessageExecuted<T>()`, `ThenMessagePublishedWithNoRoute<T>()` (NoRoutes), `ThenTheActSucceeded()`/`ThenNoExceptions()`, and an act for `PlayScheduledMessagesAsync` (e.g. `WhenScheduledMessagesPlay()`), plus a `GivenReceived(message)` arrange that renders as Given.
- WolverineFx.Bobcat (wolverine#5077, from bobcat#674): `WhenReceived` of a scheduled/timeout message should execute it now or refuse, rather than reschedule it and report success.
- WolverineFx.Bobcat.Http (wolverine): a response-header step (`ThenResponseHeader("Location", …)`).
- WolverineFx.Bobcat.Http (wolverine): depending on Alba 9.0.0-rc.2 forces a prerelease Alba major on consumers (ConfigurationOverride removed).
- Bobcat: `WhenReading<List<T>>` / `IReadOnlyList<T>` should stamp `T` as the read model (or keep the GET trigger) so a collection GET is a Query slice.
- Bobcat: diagnostic when `[BobcatSpec(typeof(X))]`'s act sends a different command than X, and when specs on one slice disagree on the command.
- Bobcat: don't emit a pattern-less, command-less slice for a spec whose only roles are `Document`.
- Bobcat: `BOBCAT_SPEC_PREVIEW` should render vocabulary calls (role/trigger-attributed members), not only `[Given]/[When]/[Then]` helpers.
- ai-skills: transport-stubbing caveat, documents/saga arrange guidance, async `TheResponse<T>`, Alba 9 note.
