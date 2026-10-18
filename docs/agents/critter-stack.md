# The Critter Stack helpers and CritterStackScenario

### The Critter Stack helpers are store-agnostic (`src/Bobcat/CritterStack/`)

Decision of record 2026-08-20 (issue #103): Bobcat's event-sourcing helpers bind to the
**`JasperFx.Events` abstractions**, never to Marten — the same discipline `Wolverine.CritterWatch`
lives by — so one package serves Marten, Polecat and Fisher. They live in **Bobcat core** (the
`Bobcat.CritterStack` namespace), and reach the store through the `JasperFx.Events` package; **core
does not reference Marten, Polecat or Fisher**, and a spec project using it needs none of those
either (`samples/BankAccountES/Tests` has no `using Marten`). The document side (`DocumentStores`)
goes through `JasperFx.Events.Documents`.

- **The store comes from the host's container, not from a Bobcat resource type.** Marten
  (`AddMarten`), Polecat (`AddPolecat`) and Fisher (`AddFisher`) all register their store as
  `JasperFx.Events.IEventStore`; `services.EventStore()` (`CritterStackHostExtensions`) resolves it
  from the host's services. `storeName` (matched on `IEventStore.Identity.Name`) disambiguates a
  host with several stores; with no `storeName`, the host's one **main** store is the default (see "Several event
  stores" below).
- **Everything the abstractions cover goes through them.** Streams are read via
  `IEventStore.OpenReadOnlyEventStore()`; projection waits via
  `IEventDatabase.WaitForNonStaleProjectionDataAsync` (the same wait JasperFx's
  `ProjectionScenario` performs) and `AllProjectionProgress`, so it does not matter who runs the
  daemon — Marten's `IProjectionCoordinator`, Fisher's hosted service, or Wolverine. A projection
  wait decides *what* to wait on from the store's configured shards (`IEventStore<,>.AllShards()`),
  not from the progress table: a daemon writes a shard's row only after its first batch, so right
  after the first append an empty table is indistinguishable from "no async projections" and a wait
  keyed on it passes vacuously. A name matching no configured shard throws and lists them.
- **One tenant's wait asks the store first** (`AfterAsyncDaemonsCatchUpAsync(tenantId)` →
  `EventStores.WaitForNonStaleProjectionsAsync(store, tenantId, …)`, wolverine#4962 phase 2): JasperFx 2.85.0's
  `IEventStore.FindDatabaseForTenantAsync` (a default member — the single database when certain, else null),
  then Marten's `Storage.FindOrCreateDatabase` by reflection for a Marten that predates the override, then every
  database. `EventStoresTests` pins all three with a fake store that re-implements the default member.
- **Two gaps in JasperFx.Events 2.53.0 are bridged by convention, and say so.** There is no
  abstraction for *aggregating* a stream or for *wiping* a store. `EventStores.AggregateStreamAsync`
  uses the read-only view when it is an `IQueryEventStore` (Marten, Polecat) and otherwise opens a
  session through the `IEventStore<TOps,TQuery>` closure and finds its `Events` member (Fisher).
  `EventStores.ResetAllDataAsync` follows `Advanced.ResetAllData(ct)` / `ResetAllDataAsync(ct)` /
  `Advanced.Clean.DeleteAll*` — the shape all three stores share — and a store matching none gets an
  exception naming what was looked for. JasperFx's own `IStatefulResource.ClearState` is *not*
  enough: Marten's database does not implement `IDatabaseWithRewindableState`, so `jasperfx
  resources clear` is a no-op for it (`ClearStatefulResourcesAsync` is still offered, for
  Wolverine's envelope storage). Both are a bounded, documented softening of "no reflection"; when
  the abstraction lands upstream, the convention path is what gets deleted.
- **`ProjectionScenario<,>` is not what the waits delegate to, and that is a decision, not a gap
  (2026-08-21, JasperFx.Events 2.53.0).** Checked on the bump: it is a *scripted* harness — it wipes
  data (`DeleteExistingData` defaults to true), builds and owns its **own** daemon, appends through
  its **own** session, and is reached only through each store's `Advanced`
  (`EventProjectionScenario` on Marten and Polecat, `EventProjectionScenarioAsync` on Fisher), each
  closed over that store's session pair with no non-generic interface and no `IEventStore` accessor.
  A Bobcat spec appends through the application's own handlers and waits on the host's daemon, so
  the right tool is what the scenario itself calls after each batch —
  `IEventDatabase.WaitForNonStaleProjectionDataAsync` / `IProjectionDaemon.WaitForNonStaleData` —
  which is what `EventStores` already does. Nothing in 2.53.0 retired a convention path either:
  there is still no aggregate-stream member on `IReadOnlyEventStore` (Fisher's view is not an
  `IQueryEventStore`, so `AggregateStreamAsync` still opens a session through the `IEventStore<,>`
  closure — proved on the real store) and still no reset abstraction (`Advanced.ResetAllDataAsync`
  is Fisher's spelling — also proved). The `IProjectionCoordinator` fallback stays for a store that
  does not override `IEventStore.AllDatabases()`; all three do.
- **Fisher is the inner-loop target, and is covered by the samples.** The current aligned version
  set is the header of `src/Directory.Packages.props`. `Bobcat.CritterStackScenario.Tests` covers
  the convention paths against fakes shaped like each store (`EventStoresTests`) and the scenario on
  real Marten (Postgres 5445, `[PostgresFact]`) and real Fisher (temp SQLite), with no Wolverine;
  the samples' Fisher leg is BankAccountES. The Fisher host needs
  `ApplyAllDatabaseChangesOnStartup()` registered *before* `AddAsyncDaemon` — Fisher builds its
  schema lazily and the daemon reads the progression table on start
  (`design/critter-stack-interop-notes.md`). Polecat needs SQL Server and is a documented manual
  run: `AddPolecat(...)` registers `IEventStore` like the others and its `ProjectionScenario` and
  reset share Marten's spellings, so the same code path applies.
- **Acceptance (#103): `samples/BankAccountES` runs on Marten and on Fisher — the same specs on each
  — with no store-specific Bobcat reference, switched by `EventStore=Marten|Fisher` in
  configuration.** The host was rewritten to the store-agnostic vocabulary (`[DeciderFunction]`,
  `[Entity]`, `Storage.StartStream`, `IEventStoreOperations`, `IDocumentReadOperations`, a
  self-aggregating `Snapshot<T>` read model in place of a `SingleStreamProjection<,>` subclass) so
  that `Program.cs` is the only file naming a store; the swap table is in
  `design/critter-stack-interop-notes.md`. The Fisher leg runs in CI (the `TestSamplesOnFisher`
  target, via `samples.yml`), because a SQLite file needs nothing the runner does not have — which
  is the whole argument for Fisher as the inner loop.

#### `CritterStackScenario` — the store-only vocabulary (bobcat#471)

Decided as **Option A** on #471: a code-first scenario in core
(`src/Bobcat/CritterStack/CritterStackScenario.cs`, with `CritterStackSpec` as the xUnit/TUnit base
class and `CritterStackActOutcome` as the act's capture) that needs **no Wolverine** — plenty of
applications use Marten, Polecat or Fisher without it. WolverineFx.Bobcat's `WolverineScenario`
derives from it and adds only the acts that go through the tracked session, the message assertions,
the DCB boundary steps and correlation.

- **Public signatures and step text are `WolverineScenario`'s, verbatim**, so deriving was "delete
  duplicates" with no API break: Givens (tagged, strong-typed-id, aggregate-less, read
  model, and since #502 `GivenContext` — `GivenEvents`'s append under `SliceRole.Context`, which never
  takes over the scenario's own stream `_stream` from a `GivenEvents`), the event assertions over the last act, the refusals, aggregates/documents/read models,
  DCB models, `Verify`/`ThenMatches`, `ResetAsync`, `storeName`.
- **The seams a derived scenario overrides:** `runActAsync(Func<IServiceProvider, Task>)` (the
  store-only act: high-water mark, run in a new scope, read back every event since, attributed by
  stream (#503, `attributeToSpecification`) — so `NewEvents == AllNewEvents`, `ForeignEvents` the
  rest, `StartedStreams` from version-1 events, `MessagesSent` empty, `EventTagsKnown = false`), `failureOf(message)` (the exception a wrong is thrown as outside a
  recording — `SpecAssertionException` here, Wolverine's `SpecificationFailedException` there), the
  protected `LastAct` setter, `ResetAsync` (virtual), and the protected helpers its own steps reuse
  (`actSucceeded`, `failureSuffix`, `verifySet`, `flatten`, `describe`, `recordValues`, `anyOf`,
  `tagsOf`, `isOn`, `unwrap`, `describeTypes`, `exceptionsIn`, `hasStore`, `recordTouched`,
  `tagsCanBeJudged`, `fail`/`fact`, and #503's `claimStream`, `attributeToSpecification`,
  `crossTalkSuffix`).
- **Store-only attribution (#503).** The scenario's own streams are every stream a Given arranged
  (`GivenContext` included), `WhenActingOn` addressed, or an act wrote to or started; an event on any
  other pre-existing stream is `ForeignEvents`. Whole-store steps (`ThenEvents*`, `ThenEmitted`,
  `ThenNotEmitted`, `ThenNoEvents`, `TheEvent`, `ThenCommandRefused`, the started-stream steps) judge
  only the own events and, **when they fail** with `ForeignEvents` non-empty, append "cross-talk
  suspected: N event(s) …"; a passing one stays quiet. Stream- and tag-addressed steps
  (`ThenEventsOn`) look at own + foreign (`addressable()`), since naming the stream already scopes them.
  Residual, documented: a stream another test *starts* during the act still counts, so whole-store
  assertions on a shared store still need serial execution (BankAccountES keeps
  `Parallelization(Mode = ParallelMode.None)`). DCB: no store reads tags back, so a tagged append is attributed by its
  stream; where tags are returned, a tag value the spec arranged events with also counts.
- **No store reads an event's tags back** (checked on Marten and Fisher), so after a store-only act
  a step that judges tags fails saying so (`tagsCanBeJudged`) instead of reporting every tagged
  event MISSING. Name the stream, or read the boundary's model with `TheDcbModel<T>`.
- **Two refusal styles, two steps (issue #168).** `ThenValidationFails(reason)` /
  `ThenRefusedWith(reason)` describe a refusal that *throws*. A handler that refuses *without*
  throwing (a messaging railway's `Stop`) is `ThenCommandRefused()`: the act ran, nothing threw,
  nothing was appended. Deliberately **reason-less** — a clean stop carries no reason anywhere an
  act surfaces, so a reason clause would assert on nothing.
- **Also on the scenario:** the stream-version steps (`TheStreamVersion`, `ThenEventsOn<T>(id,
  afterVersion, …)`, `ThenNoEventsOn<T>(id, afterVersion)`, `WhenActingOn<T>` as one `Paragraph`); a
  configurable `ProjectionTimeout` (**20 s**); refusal-reason matching across inner and aggregate
  exceptions, compared in **one** place (`ReasonComparison`, Ordinal until exact-vs-ignore-case is
  decided); `ThenMessagesSent<T>(count)` over `CritterStackActOutcome.MessagesSent` (always empty
  for a store-only act, filled by a bus); and the clock steps, recorded over `BobcatClock`
  (`GivenTheDateIs`, `GivenTheTimeIs`, `WhenTheClockAdvancesBy`, `WhenTimePasses`,
  `WhenTheClockAdvancesTo` — synchronous on purpose, because the clock is `AsyncLocal` and a value
  set inside an async method never flows back).
- **Catch-up timeouts before a read are errors that say why (#497, decided C).** `ThenDocument`,
  `ThenReadModel`, `ThenNoReadModel`, `ThenSingleReadModel`/`ThenSingleDocument` and
  `ThenProjectionsAreCaughtUp` wait through `waitBeforeReading`, which rethrows a store's
  `TimeoutException` as `ProjectionCatchUpTimeoutException` (still a `TimeoutException`, so the step is
  `error`, never `failed`): the wait, `EventStores.ProjectionsBehindAsync`'s best-effort
  `'shard' at N of M` list, the timeout, the type and identity read, the original as `InnerException`.
  The #497 root cause: Marten's `WaitForNonStaleProjectionDataAsync` throws an **immediate**
  `TimeoutException` ("No event activity was detected") on a store with no events, because a fresh
  `mt_events_sequence` reports last value 1 with zero events. So `ThenDocument` on a fresh schema threw
  when the test ran first in its class (xUnit's order follows the assembly path) and passed after any
  test had appended. `WaitForNonStaleProjectionsAsync` now returns at once for a single-database store
  whose read-only view holds no events; `reading_an_empty_store_on_marten` pins it in a class of its own.
- **Run evidence (#465):** `ScenarioRecorder.RecordTouchedType(Type)` is an ambient accumulator on
  the recording, static and `AsyncLocal`-backed for the reason `SpecReport` is (#408);
  `Recording.TouchedTypes` reaches `scenario_finished.TouchedTypes` through `MonitorTouchedTypes`,
  so Stoat's MCP/API field keeps its shape. Observed, never asserted: arranged aggregates and
  events, the events an act appended, the models and documents loaded — never a type a Then step
  only expected.
- **Tested in `Bobcat.CritterStackScenario.Tests`**: every member on Marten (Postgres 5445, a schema
  per host) and on Fisher (temp SQLite), with no Wolverine anywhere in the project.

#### Several event stores in one specification (bobcat#504, decided A + B + C)

- **A, route by type.** On a host with several `IEventStore`s every Given and Then goes through the protected
  `storeForAsync(step, model, values, tagTypes)`: the one store whose registrations claim the model type (an
  aggregate, projected or document type), else its events' or tags' types, else the default store (the
  constructor's `storeName`) when it is among the claimants or nobody claims it, else a `SpecCriticalException`
  listing the candidates and naming `In<TStore>()` / `In("name")`. Ownership is read store-agnostically by
  `StoreCatalog` (`StoreRouting.cs`) from `IEventStore.TryCreateUsage` (subscriptions' `AggregateType` and
  `AppliedEvents`, `Events`, `RegisteredEventTypes`, `TagTypes` and their aggregates) and
  `IDocumentStoreUsageSource.TryCreateUsage` (`Documents`); a store that returns null or throws owns nothing.
  Catalogs and decisions are cached per specification (`StoreRouter`). Stores register some types lazily (an
  event type on first append, a document mapping on first store or load), so ownership can grow during a run.
  **A single-store host never reaches the router**: `storeForAsync` returns `EventStore` before anything is read.
  A raw stream addressed by id alone (`TheStreamVersion(id)`) has no type and goes to the default store.
- **B, explicit addressing.** `In<TStore>()` resolves the marker from DI. If that is not itself an `IEventStore`
  (Fisher registers a `DispatchProxy` under the marker and the unwrapped store as `IEventStore`), it falls back to
  the store whose `Identity.Name` equals the marker's type name. That is the default name all three stores give
  an ancillary store: Marten and Polecat set `StoreName = typeof(T).Name`, and Marten and Fisher lower-case it in
  the identity. `In(name)` matches `Identity.Name`, ignoring case. Both return a **view**: a `CritterStackScenario`
  built by the protected `CritterStackScenario(root, store)` constructor, pinned to that store and sharing the
  root's private `SpecificationState` (own streams, arranged tags, last act, `ProjectionTimeout`,
  `UnspecifiedValues`, the router). A view's `runActAsync` and `failureOf` delegate to the root's, so an act
  through a view is not split and is not recorded twice. `createView(store)` is virtual, so a derived scenario can
  hand out its own view type. `CritterStackSpec.In` forwards.
- **C, every store observed.** `runActAsync` is `highWaterMarksAsync()` (one `StoreHighWaterMark` per store
  from `storesToObserve()`), then the act, then `readBackSinceAsync(marks, error)`. That reads each store since
  its mark, applies #503 attribution per store (one shared own-stream set, keyed by stream id), lists events
  store by store, and fills `CritterStackActOutcome.StoreByEvent` / `StoreOf(e)` only when more than one store
  was observed. Whole-store failures append `storesSuffix()` ("Appended by store -- 'main': …"). Stream- and
  tag-addressed steps order the addressable events store by store, then by sequence.
  `AfterAsyncDaemonsCatchUpAsync` waits on the default store plus every store the specification **touched**
  (`SpecificationState.Touched`: a routed step's store, a view's store from `createView`, a store an act appended
  *own* events to in `readBackSinceAsync`; cleared by `ResetAsync`), or on every store when there is no default
  (a view, on its own). `waitBeforeReading` waits on the store being read. Read-back still observes every store.
- **The main store is the default (CritterWatch embedded).** `AddCritterWatchEmbedded` registers the
  console's own ancillary Marten store (`AddMartenStore<ICritterWatchStore>`, identity `icritterwatchstore`), so a
  spec host had two stores and every unqualified step was refused. Now, with no `storeName`,
  `CritterStackHostExtensions.EventStore()` / `DefaultEventStore` take the only store, else the **one main
  store**, else throw (message lists each store as `[main]`/`[ancillary]` and names `storeName`, `In<TStore>()`,
  `In("name")`). The router's default and `EventStore` use the same rule. **The signal is the registration, not the
  name:** `IsMainEventStore` finds the non-generic interface named `IDocumentStore` on the store's own type
  (Marten's, Polecat's, Fisher's) and checks that the container resolves it to that very instance. All three
  register their main store under `IDocumentStore` and an ancillary store under its marker only, so for an
  ancillary store that service is another instance or nothing (Fisher's unwrapped ancillary store too; Polecat
  does not put ancillary stores in `GetServices<IEventStore>()` at all). Rejected: `Identity.Name == "main"`
  (only the default `StoreName`, user-settable), `Identity.Type` (the product, not the role), the
  non-generic `IProjectionCoordinator` (exists only with a daemon). A store with no `IDocumentStore`
  interface (fakes, in-memory) is never main, so such hosts keep the refusal. Waits ignore an untouched
  ancillary store (above), so CritterWatch's store cannot stall a spec; its projections are all inline today
  anyway, so Marten's wait returns at once for it.
- **Not decided, so not built:** whether the Event Model records which store a slice's aggregate lives in
  (a JasperFx descriptor change); and an event type registered in more than one store, which is addressed
  with B only (routing by events then fails with the candidate list).
- **Tested:** `store_routing` (fakes, no database: the routing rules, caching, the single-store no-read
  guarantee, views, marker fallback), `default_main_store` (fakes registered under a fake product's
  `IDocumentStore` or a marker: main beside ancillary is the default, two mains or ancillary-only refuse, one store
  is the default whatever its role, waits skip an untouched ancillary store), `ancillary_stores_on_marten` /
  `ancillary_stores_on_fisher` (a real main + ancillary store on each, unqualified steps going to the main store),
  and `main_store_default_on_marten` (an ancillary store whose async projection never runs: an unqualified spec
  passes and its wait never touches that store, while `In<IConsoleStore>().AfterAsyncDaemonsCatchUpAsync()` times
  out). Polecat has the same `AddPolecatStore<T>` / `IDocumentStoreUsageSource`
  surface but needs SQL Server, so it is a manual run, as elsewhere.
- **WolverineFx.Bobcat follow-ups (wolverine#4977):** `WolverineScenario.runActAsync`'s high-water fallback
  still reads one store and should use `highWaterMarksAsync` / `readBackSinceAsync`. Its tracked-session
  outcome cannot fill `StoreByEvent`. `ThenEventuallyCaused` and its other own steps still read `Store` (`EventStore` since bobcat#709). Views
  are plain `CritterStackScenario`s, so its message and boundary steps are not on them until it overrides
  `createView`. `WolverineSpec` needs `In`.
