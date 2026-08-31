# Bobcat.Console, the test-run viewer — design notes

A deployable web console (`dotnet bobcat`) that shows live progress for every Bobcat test suite
running on the box. Primary purpose: visualizing AI-agent-driven test runs — much of Critter
Stack development is gated on testing time, and this makes that time observable.

Decisions of record (2026-07-31), amended by the Bobcat/Stoat split (2026-08-09) and by the
rename to `Bobcat.Console` (2026-08-21, issue #100).

## The name (2026-08-21)

The project is **`Bobcat.Console`** (`src/Bobcat.Console`, `Bobcat.Console.Tests`,
`Bobcat.Console.Specs`, `Bobcat.Console.FrontEnd`; namespaces `Bobcat.Console.*`; NuGet package
id `Bobcat.Console`, tool command still `bobcat`). It was `Bobcat.Monitor` from 2026-07-31.
`Bobcat.Viewer` was the other candidate and lost because nobody calls it a viewer out loud —
"console" is the word already in the tool's description, in this document, and in every
handoff.

Two things stay as they were, on purpose, and this file's name is the first of them:

- **`docs/monitor-design.md` keeps its filename.** It documents the monitor *protocol* — what a
  run publishes, over which routes, with which env vars — at least as much as the viewer that
  receives it, and issues, handoffs, and the CLAUDE.md seams all link to it by this name.
- **The publisher side in core keeps the monitor vocabulary:** `Bobcat.Monitoring`,
  `BobcatRunner.PublishToMonitor`, `MonitorPublisher`, `BOBCAT_MONITOR*`, `BOBCAT_RUN_ID`,
  `BOBCAT_RUN_TAG`, the `Monitor:*` config keys, the `/api/*` routes, the wire shapes and the
  duplicated `MonitorEvents.cs` records. There "monitor" means *the thing a run publishes to*,
  and every one of those is a user-facing or wire contract. Renaming them would be a breaking
  change that #100 explicitly scoped out.

Mentions of `Bobcat.Monitor` in dated history below were rewritten to the new name wholesale;
read them as the same project.

## The split (2026-08-09)

This document once described two futures for the tool. Both are now settled, and neither
happened the way it was written:

- **The AI agent coordination surface moved out** to [Stoat](https://github.com/JasperFx/stoat),
  its own BSL repository. Bobcat stays MIT and stays about testing. Stoat observes this
  viewer's runs over HTTP (`GET /api/runs`) exactly as it observes GitHub and nuget.org — it
  holds no reference to any `Bobcat.*` assembly, and none of them reference it.
- **The rename is dead.** The plan was for this tool to become "Bobcat" and the library to be
  renamed. The split makes that unnecessary: Bobcat keeps its name and its meaning as the
  testing framework, and the coordination half got a new name instead. Issue #87 closes
  resolved-by-decision.

What the split changed here, concretely:

- `ToolCommandName` is **`bobcat`**, not `bobcat-monitor` — "monitor" turned ambiguous the
  moment there were two consoles, and this is the only global tool Bobcat ships. Note that
  `run` / `list` are *not* subcommands of it: those belong to `BobcatRunner` inside a
  consumer's own test executable, because they need that project's compiled fixtures.
- `BOBCAT_PLAN_NODE` became **`BOBCAT_RUN_TAG`**, an opaque correlation tag Bobcat stamps and
  never interprets. Coordination vocabulary does not belong in the MIT repo, and a general tag
  is more useful anyway (a ticket id, a build number, an external tool's node id).
- **`GET /api/runs` is now a public wire contract**, not just the dashboard's list model. It
  carries the tag, outcome counts, and scenario progress, and takes a `?tag=` filter — an
  external consumer correlating its own work to a suite has no other way in. Reads go through
  the registry's locked `ReadAll` so live ingestion can never hand a caller a torn scenario
  collection.
- **NDJSON stays.** The old plan demoted it to an export format once a shared event store
  landed. That rationale was "one store, not two" for runs plus coordination; with coordination
  gone there legitimately are two tools, and the run archive is fine as it is (issue #90).

## Stack — CritterWatch's, on purpose

Vue 3 + Pinia + Element Plus + `@microsoft/signalr` frontend, ASP.NET + Wolverine +
Wolverine.SignalR backend, mirroring `~/code/critterwatch`:

- One `HubConnection` owned by `useSignalR.ts`, retry-forever backoff, rAF-batched flush into
  `relayToStore.ts`, which switches on snake_case envelope types and fans out to Pinia stores.
  Stores never touch SignalR. The rAF flush has a plain-timer backstop because rAF never fires
  in a hidden tab — without it a backgrounded (or headless) dashboard queues events until
  refocus, which also breaks any headless e2e against the UI (found live 2026-07-31).
- Color tokens in `src/styles/variables.css` — the JasperFx orange Element Plus ramp, `--bm-`
  prefix. The test-state grammar (running blue / passed green / failed red / retrying orange)
  is adapted from CritterWatch's Event Modeling grammar.
- Backend flow (built 2026-07-31): the `[WolverinePost]` ingestion endpoint folds into the
  registry, then queues events into `SignalRBatchAccumulator` — CritterWatch's 100ms
  accumulator, lifted but simplified: ingestion is the only producer, so the endpoint feeds it
  directly (no per-type relay handlers, no static-instance hack). Each flush publishes one
  `BatchedWebSocketPayload : WebSocketMessage` (so the existing publish rule routes it and
  Wolverine's WebSocketMessage naming yields `batched_web_socket_payload` with no attribute;
  no loop risk because no publish rule feeds the accumulator). `relayToStore` unwraps the
  `{type, data}` items recursively; wire names are pinned to the STJ discriminators by
  `SignalRBatchingTests`.
- TS mirrors of the contracts are **generated** (issue #85, built 2026-08-21): CritterWatch's
  NJsonSchema `GenerateCommand` pattern, cut down. `TypeScriptContracts` in `Bobcat.Console`
  reflects `MonitorEvents.cs` through the same STJ settings the wire uses and emits
  `src/messages/monitor-events.ts` wholesale (interfaces `extends MonitorEvent`, a
  `MonitorEventType` union, the batching envelope); `relayToStore.ts` is *patched*, not owned —
  a missing `case` is inserted above the `*CASE ABOVE*` marker in the store's
  `handle<Type>` convention and the import block is merged, while hand-written cases stay
  verbatim. Regenerate with `dotnet run --project src/Bobcat.Console -- generate` (`--check`
  verifies only). `TypeScriptContractTests` fails `dotnet test` when either committed file
  differs from what the records generate, so drift is a red build. Two deliberate rules: a
  record constructor parameter with a default (`RunStarted.Tag`) mirrors as an *optional*
  member, because "additive" means an old publisher's JSON has no such member at all; and the
  Bobcat-side publisher mirrors (`src/Bobcat/Monitoring/MonitorEvents.cs`) are NOT generated
  or unified — that duplication is a decision of record kept honest by `ContractRoundTripTests`.
- No Aspire. The Vite dev server proxies `/api` (ws included) to the host's fixed dev port
  5525. Bobcat will eventually need an Aspire *resource recipe* as a testing feature; that is
  unrelated to this tool's dev workflow.

Packaging (built 2026-07-31): `dotnet tool` (`ToolCommandName: bobcat`, launched as `dotnet bobcat`) with the Vite
build embedded as resources — CritterWatch's `EmbedFrontend` + `EmbeddedFileProvider` pattern
(`Hosting/EmbeddedSpa.cs`, minus its sub-path mounting; this tool owns the root). Two rules of
record: `IsPackable` is gated on `EmbedFrontend`, so the tool nupkg cannot exist hollow (a
solution-level `dotnet pack` simply skips the project — publish.yml packs it explicitly); and
the EmbeddedResource items are created INSIDE the BuildFrontend target, because a static glob
evaluates before the Vite build runs and silently embeds nothing on a clean build.

## Transport: HTTP, fire-and-forget, never slows a run

Publishers (BobcatRunner, the supervisor, worker processes) POST batches of events to
`/api/ingest`. HTTP over raw TCP because the emitting client must be dependency-free
(`HttpClient` + STJ only — no Wolverine in Bobcat), and events arrive at tens/second, not
thousands. The invariant that outranks all others: **a test run is never slowed or failed by
the monitor.** Probe `GET /api/ping` once at startup with a tight timeout → publisher goes
no-op for the run if nothing answers; bounded channel, drop on backpressure; discovery via
`BOBCAT_MONITOR_URL` (default `http://localhost:5525`).

## Event model

`src/Bobcat.Console/Contracts/MonitorEvents.cs` — polymorphic `MonitorEvent` records. The STJ
type discriminator and the Wolverine message type name are pinned to the same snake_case
string, so ingestion JSON and the SignalR envelope agree by construction. Identity: `RunId`
(minted per run) + scenario uid `"{Feature}/{Scenario}"` — the string BobcatRunner,
`RetryBudget`, `SpecNodeMapping`, and `WorkPlan` already share. `RunStarted` carries the root
repository path + branch, the dashboard's grouping key for parallel suites on one box.
`RunHeartbeat` exists so a crashed/orphaned run renders as such instead of "running" forever.

The publisher client lives in Bobcat as `Bobcat.Monitoring` (issue #65): mirror records of
these contracts, DECIDED to stay deliberately unshared — Bobcat must not depend on the
monitor's Wolverine stack, and the wire shape (not an assembly) is the contract. The
round-trip tests in `Bobcat.Console.Tests` are what keep the two sides honest.

## Bobcat-side seams (issue #65 — built)

1. **`CompositeObserver` + `BobcatRunner.AddObserver`** — observers fan in additively, so the
   monitor publisher rides alongside the MTP `PublishingObserver`. `WithObserver` keeps
   replace semantics. An observer throwing never fails the run or starves other observers.
2. **`MonitorPublishingObserver` + `MonitorPublisher`** (`src/Bobcat/Monitoring/`) — maps
   observer callbacks (plus the new `RunStarted`/`RunFinished` run bracket on
   `IExecutionObserver`) onto the wire events; fire-and-forget HTTP with a bounded
   drop-on-backpressure channel; probes `/api/ping` once and no-ops when absent.
   `BOBCAT_MONITOR_URL` overrides the target, `BOBCAT_MONITOR=0` is the kill switch.
   Publishing is **opt-in** (`BobcatRunner.PublishToMonitor`) and turned on only by the real
   entry points — `BobcatRunner.Run` and the MTP host's execution path (never discovery) — so
   unit tests driving the runner never probe. `BOBCAT_RUN_ID` seeds the run identity so a
   supervisor can group its workers' streams without supervisor changes.
3. **Supervised-run grouping** (built 2026-07-31, once supervisor work reopened): the
   supervisor is the run's monitor-facing OWNER. `Supervisor.PublishToMonitor` (opt-in, same
   policy and probe as the runner's) posts the run bracket itself via
   `SupervisorRunPublisher` — RunStarted with mode `supervised` and the true post-filter test
   total (which no single worker knows), heartbeats, and a RunFinished whose counts include
   `Indeterminate` (never folded into Failed — same split as exit 2 vs 1). Every worker
   launch — discovery included — inherits `BOBCAT_RUN_ID` + `BOBCAT_RUN_OWNER` via
   `WorkerLaunchContext.Environment`, the LOWEST layer of the env stack (factory shared env
   and `EnvironmentFor` both override it). `BOBCAT_RUN_OWNER` is deliberately a second
   variable: a worker seeing it suppresses its own bracket (else the first worker to finish
   would mark the shared run finished with partial counts), while `BOBCAT_RUN_ID` alone still
   just pins identity for a standalone run that keeps its bracket. A cancelled/crashed
   supervisor posts no RunFinished — heartbeats stop and orphan detection tells the truth.
4. **`ISupervisorObserver`** (built 2026-08-02, issue #84) — the supervisor's live narration:
   `AttemptRecorded` (every attempt, passes included, with the policy verdict that followed
   it), `RetryScheduled`, `LaneStarted`/`LaneFinished`, `ResourceRecycled`, `WorkerFaulted`.
   Every member is a default no-op so a consumer implements only what it wants, and an
   observer that throws is logged and stepped over — a dashboard must not be able to fail a
   test run. `SupervisorRunPublisher` is one, registered automatically when
   `Supervisor.PublishToMonitor` is on.
   - **Retry topology is on the wire**: a supervised retry now posts `RetryScheduled` with the
     disposition and reason, announced *after* the budget and the resolve step have had their
     say — a retry that was requested and refused never reaches a watcher as though it were
     about to happen.
   - **The attempt number is the load-bearing part.** A worker counts from one:
     `MonitorPublishingObserver`'s tracking belongs to a `BobcatRunner`, and the MTP host
     builds a fresh runner per run request, so a retry in a brand-new process and a retry in a
     reused one both announce attempt 1. The supervisor holds the only true count, so
     `RetryScheduled.NextAttempt` **pins** the number the next `ScenarioStarted` folds as —
     in `RunProjection` and in the Pinia store identically. Taken as a *floor*, never an
     assignment: an attempt number never goes backwards, because hydration routinely replays
     a start for an attempt already watched. `ScenarioFinished.Attempts` gets the same floor.
     Before this, a supervised retry overwrote its own previous attempt and CTRF's
     `retryAttempts[]` worked for in-process retries only.
   - **Lane topology, recycles and worker faults are on the wire** (built 2026-08-21, the
     rest of #84): `LaneStarted` (lane + the uids it was handed), `LaneFinished` (outcomes
     reported, `Crashed`), `ResourceRecycled`, and `WorkerFaulted` (lane or null for a
     one-test process, the report's sentence, **exit code and last standard error as separate
     fields**), each stamped with the supervisor's clock. `SupervisorRunPublisher` posts them
     from the observer callbacks; `ISupervisorObserver` gained a structured
     `WorkerFaulted(WorkerFault)` whose default forwards to the original `WorkerFaulted(string)`,
     so an observer written against either keeps working. A lane starts again for a
     same-process retry (back to the lane the test ran in, carrying only the retried uids) —
     the store counts that as a second *pass* of the same lane; isolated and recycled retries
     are one-test processes and never announce a lane, so a foreign-framework worker's lane
     events are the only live signal it has. Folded in the Pinia runs-store as `lanes` (lane
     order, with "running now" = the lane's uids joined to live scenario state), `recycles`
     and `faults` on the run; rendered by `LaneStrip` on the card and `SupervisorTopology` on
     the detail. Replay-safe by the supervisor's timestamps: a lane start no newer than the
     pass we are on, a finish older than that pass, or a recycle/fault already seen is the
     archive being re-announced over live state, not a new fact.
   - **Folded server-side too** (built 2026-08-21, the last piece of #84): `RunProjection`
     carries `Lanes` / `Recycles` / `WorkerFaults` (+ `RunningIn(lane)`, the lane's uids
     joined to live scenario state) under exactly the store's rules —
     `SupervisorTopologyProjectionTests` is a case-for-case port of
     `runs-store-topology.test.ts`, so the two folds cannot drift silently, and it includes the
     same replay-over-live-state no-op. Read by MCP `run_status` (`lanes`, `recycles`,
     `workerFaults`, always present — empty arrays for an in-process run, so an agent never
     has to guess whether the field is missing), by `GET /api/runs/{id}` (`RunDetail.Lanes` /
     `Recycles` / `WorkerFaults`, additive init properties), and by the CTRF export in the
     results-level `extra` (`lanes`, `recycles`, `workerFaults`, omitted for an in-process run
     so that export is byte-identical to before; CTRF has no vocabulary for worker processes
     and the schema would reject an invented top-level field). One consequence for the
     scenario fold: a supervised retry's first attempt reported its own terminal outcome, so a
     genuinely new attempt's `ScenarioStarted` now clears `Outcome` — the retried scenario reads
     as running again, which is what a lane's "running now" and `run_status` need. Only a new
     attempt clears it (attempt numbers are a floor), so a replayed start never un-finishes one.
     A crashed lane's scenario that never reported an outcome keeps reading as running — the
     fold infers nothing for it; the supervisor's `RunFinished` is what counts it Indeterminate.
   - **The observability cluster is on the wire too** (built 2026-08-24, issues
     #145/#146/#148/#149): three additive events posted by `SupervisorRunPublisher` from the
     cluster's observer callbacks. `worker_started` (purpose, lane or null, **pid**) — a
     discovery worker is deliberately never announced, because it launches before the run
     bracket opens and `run_started` stays the stream's first event; its pid folds onto the
     lane, so lane→pid correlation needs no `/proc` guessing. `test_stalled` (uid, display
     name, in-flight ms, lane, pid) — once per attempt, the name a capped CI job's log cannot
     produce. `run_progress` (elapsed, done/total, in-flight count, the longest-running test,
     and peak worker RSS when memory sampling is on — null otherwise, unmeasured is never
     zero) — posted only when the supervisor's opt-in `HeartbeatInterval` is set, distinct
     from `run_heartbeat` which stays a bare liveness ping; for a foreign-framework worker
     this is the run's only live progress. Folded on both sides under the same rules (`stalls`
     replay-guarded by uid+timestamp; `progress` latest-wins, ordered by the supervisor's
     elapsed clock so a replayed older heartbeat never rolls it back; a replacement worker's
     own start moves the lane's pid) — mirrored case-for-case between
     `SupervisorTopologyProjectionTests` and `runs-store-topology.test.ts` like the rest of
     the topology. Read back by `GET /api/runs/{id}` (`RunDetail.Stalls`/`Progress`,
     `LaneResult.ProcessId`, all additive) and MCP `run_status` (`stalls` always present,
     `progress` nullable, `processId` per lane); rendered by `SupervisorTopology` (progress
     line, pid column, stalled list).
   - `MtpWorkerClient.handleNotification` receives live per-test `testing/testUpdates/tests`
     updates; since #99 it relays them (see item 5) instead of reading only the outcome. A
     supervised run already gets step-level visibility because each worker IS an MTP host
     running `BobcatRunner`, and its own publisher streams steps directly to the monitor.
5. **Step-level progress for a scenario in flight** (built 2026-08-21, issue #99). Four
   additive pieces, engine to viewer:
   - **Step n of N with elapsed.** `IExecutionObserver.ScenarioStarted(feature, scenario,
     totalSteps)` is a new default member the runner calls (the plan is built before the
     scenario is announced, so the count is a fact); the two-argument form is what it forwards
     to, so existing observers are untouched. On the wire `ScenarioStarted.TotalSteps`,
     `StepStarted.StepNumber/TotalSteps/ScenarioElapsedMs`, `StepFinished.ScenarioElapsedMs` —
     all optional trailing members, null from an older publisher. "Expected" per step is
     deliberately not here: it needs the cross-run duration ledger (#44 layer 2 / #56 layer 3).
   - **Row progress for `[TableGrammar]`.** The generated envelope calls
     `ctx.ReportProgress(StepUpdate.ForRow(k, M))` before each row; `StepUpdate` gained
     `Row`/`TotalRows`. Row ticks carry no message on purpose, so the Spectre console (which
     prints every message) stays quiet while renderers with a live counter move.
   - **One wire event, `step_progress`**, for both row ticks and the `[WaitFor]` poll loop's
     interim message (#32/#34's `StepProgress` finally has a wire form): `StepId`, `Message`,
     `Row`, `TotalRows`, `ElapsedMs` since the step started. **Coalesced by the publisher** —
     `MonitorPublishingObserver` posts at most one per 100 ms per step, always the first update
     and always the last row — because 200 rows in a few milliseconds would otherwise be 200
     events into a channel that drops on backpressure, crowding out the `StepFinished` that
     matters more. Consumers upsert per step; only the latest matters, and a finished step
     ignores late (hydration-replayed) progress.
   - **The tap.** `IWorkerClient.OnTestUpdate(handler)` (default no-op) and
     `ISupervisorObserver.TestUpdated(WorkerLaunchContext, WorkerTestUpdate)` — every node
     change a worker streams, in-progress included, stamped with the lane and purpose it came
     from. Discovery is not tapped ("discovered" is not progress). Supervisor-side only for
     now; see Not built yet for why it has no wire event.
   - Viewer: `ScenarioProgress.vue` on the run detail — step n/N bar, current step text, row
     k/M bar, waiting-for message with elapsed. Store fields `ScenarioState.totalSteps`,
     `StepState.stepNumber/scenarioElapsedMs/progress`.
6. **Run evidence: touched types on `scenario_finished`** (built 2026-08-24, issue #107).
   Slice↔spec binding is by identity plus run evidence, never hand-typed — the runtime half of
   the #106 descriptor pairing. `ScenarioFinished` gained two optional trailing members:
   `TouchedTypes` (a list of `TouchedType(Name, FullName, AssemblyName)` — deliberately
   JasperFx `TypeDescriptor`'s three fields, mirrored not referenced, because the contract
   files stay dependency-free copies; `FullName` is the join key against a design-time
   `SpecificationDescriptor.ResolvedTypes`, `Uid` the identity both sides key on) and `At`
   (the finish stamp a consumer ages evidence by). Evidence is **observed, never asserted**:
   `IStepContext.RecordTouchedType(Type)` (default no-op) accumulates onto
   `ExecutionResults.TouchedTypes` in first-touch order, deduplicated, and
   `Bobcat.CritterStack`'s typed steps record at the point a type actually crossed the
   scenario — the aggregate arranged, the command dispatched (a validation rejection still
   received it), the events the stream actually gained, the messages the tracked session
   actually sent, the read model actually loaded — never what a `Then` merely names, so a sad
   path records its rejected command and no event type. Nothing recorded travels as **null,
   not an empty list** (absence of evidence, not evidence of nothing), and the folds assign
   rather than append so hydration replay cannot double the ledger. Read back per scenario by
   `GET /api/runs/{id}` (`ScenarioResult.TouchedTypes`/`FinishedAt`, additive) and folded
   into the Pinia store (`ScenarioState.touchedTypes`/`finishedAt`); CTRF/JUnit exports are
   untouched — they project explicit shapes and CTRF's schema has no vocabulary for this.

## Event Model page + /api/event-model (issue #108, built 2026-08-24)

The design-time Event Modeling viewer with spec drill-down — free, MIT, in this repo by the
2026-08-20 decision of record; CritterWatch is the production, paid surface. Both render the
same JasperFx `EventModelDescriptor` through one shared component, which is what makes "the
same descriptor renders identically in both viewers" true by construction rather than by
convention:

- **`@jasperfx/event-model-vue`** (`src/Bobcat.EventModel.FrontEnd/`, landed with #143) renders
  a descriptor with a pure synchronous layout — position is a function of the descriptor alone,
  pinned on exact coordinates by its own Vitest gate (`event-model-frontend.yml`). #108's page
  work added the `slice-click` emit (the slice header is the drill-down handle; the slice
  overlay itself stays pointer-inert so cards keep their clicks) and dropped the vestigial
  `@vue-flow/core` peer dependency — nothing in the package ever imported it, and npm 7+ would
  have installed it into every consumer.
- **The SPA consumes the package as a `file:` dependency**, and the package's `dist/` is
  gitignored — so both `console-frontend.yml` and the csproj `BuildFrontend` (EmbedFrontend)
  target build the package before the SPA, and the workflow's path filter includes the package
  so a package change re-gates the SPA.
- **`PUT /api/event-model` / `GET /api/event-model`** is a public wire contract like
  `GET /api/runs`: one descriptor document, latest push wins, persisted as `event-model.json`
  beside the run archives (`EventModelStore`). The producer is whoever has the descriptor —
  Wolverine's `event-model` export file curl'd up, or a CI step posting what a spec assembly's
  generated `IEventModelDefinitionSource` (#106) reported. The store round-trips the document
  through the typed descriptor, so a bad push 400s at the push (not as a blank canvas later),
  the stored copy is normalized to the shape the renderer's TS mirror types (camelCase members,
  PascalCase enum values — enum reads are case-insensitive so camelCase producers normalize),
  and the computed `elements`/`edges` are always present however sparse the pushed roles were.
  - **Consequence pinned in the csproj:** `Bobcat.Console` references `JasperFx.Events`
    directly, because at 2.54.0 the descriptor lives there (it moves to JasperFx only in
    2.55.0, jasperfx#693) and CPM pins only direct references — without it the transitive
    JasperFx.Events resolves to the pre-#687 sketch, which compiles and then silently drops
    `pattern`/`specifications`/`elements` on the round trip. The `DispositionKind` trap again.
- **The page** (`/event-model`, `EventModelPage.vue`): renders the descriptor, colours slices
  from run evidence — `outcomesFor` folds a selected run's scenarios onto the descriptor's spec
  identities (verdict → passed/failed; declared-but-unreached is stated as `notRun`, never
  omitted, because that is the drift colour), newest run by default with a picker. Clicking a
  slice header (or any card — ownership is an element-id lookup) opens the drawer: each bound
  spec with its verdict tag, the scenario's step results, and its touched types (#107), with
  `undeclaredTouches` flagging evidence the model does not declare — the "spec touching
  undeclared types" yellow.
- **Card sizing is decided in the package, not here (issue #180, 0.5.0).** Cards were absolutely
  sized at 180px with `overflow: hidden`, so a long command name — and worse, a route trigger
  label — was cut off mid-glyph. The order is now wrap (`<wbr>` at camel humps and after
  `/ . _ - :`), then widen the column to fit its own labels in two lines up to a `maxCardWidth`
  cap, then clamp to three lines with the full text on the tooltip. Widths are *estimated* from
  the label text, never measured, because layout must stay a pure function of the descriptor.
  One rule worth knowing: a `Hotspot` label is excluded from the width vote — the producer makes
  the hotspot's *text* the label (jasperfx#704), and letting a sentence size a column of type
  names widened every column on the real Stoat model to fit the finding rather than the model.
  Full reasoning in the package README.
- Proven end to end: `EventModel.feature` in `Bobcat.Console.Specs` drives the wire
  (404-before-publish, normalized read-back, slice↔spec binding); `EventModelStoreTests` pins
  the normalization; the page and store folds are Vitest-covered; and the flow was verified in
  the running app — descriptor PUT, run ingested with touched types, canvas coloured, drawer
  drilled.

## Ejecting results: CTRF primary, JUnit XML fallback

Researched 2026-07-31 (GitHubActionsTestLogger, MTP-native reports, CTRF, JUnit):

- **CTRF** (ctrf.io) is the primary export. It is the only CI format with first-class
  `retries`, `retryAttempts[]`, `flaky`, and `steps[]` — Bobcat's attempt history,
  `PassedOnRetry` ledger, and Gherkin steps map onto schema-blessed fields. Richer data
  (Disposition reasons, recovery hints, worker/lane ids) rides the spec's `extra` object,
  allowed at every level. Microsoft standardized on CTRF + JUnit + TRX for MTP 2.3's
  first-party report extensions, and xunit.v3 ships `--report-ctrf` working today on the
  MTP 1.9.1 pin (verified against the built `Bobcat.Tests` host), so the format is aligned
  with where the platform is going *and* usable now. `ctrf-io/github-test-reporter` covers
  GitHub PR reporting over it.
- **JUnit XML** is the lossy compatibility floor: native ingestion in GitLab, Jenkins, Azure
  DevOps, CircleCI. Accept the lossiness.
- **Not**: TRX (only AzDO wants it, and AzDO eats JUnit); a bespoke JSON (CTRF `extra`
  removes the justification); GitHubActionsTestLogger as a base (VSTest logger at the wrong
  altitude; its MTP mode needs MTP 2.x + `xunit.v3.mtp-v2`, excluded by the 1.9.1 pin).
- Persist each run's raw ingested event stream as NDJSON — export and replay-for-debugging
  both fall out of it. "Eject" in the UI = export (CTRF/JUnit/NDJSON) + remove from dashboard.

## MCP (built 2026-07-31)

`src/Bobcat.Console/Mcp/MonitorTools.cs`, mounted at `/api/mcp` — streamable HTTP, stateless,
via `ModelContextProtocol.AspNetCore` in the CritterWatch *.Mcp shape (static
`[McpServerTool]` methods returning camelCase JSON). Six tools: `list_runs`, `run_status`
(live steps for the executing scenario only), `failing_tests`, `flaky_ledger` (spans all
known runs — the box's chronic-flakiness view), `export_run` (CTRF/JUnit as a tool result),
and `await_run_completion` — the agent killer feature: block until the suite settles instead
of polling, returning `finished`/`orphaned`/`timeout` with the final summary. All tool reads
go through the registry's locked `Read`/`ReadAll` so live ingestion can never hand a tool a
torn scenario collection (exports were moved onto the same locked reads).

Live-verified: with several publishers active on the box, a no-runId await honestly latched
the one in-flight run — which happened to be another session's supervisor worker. Agents on
a busy box should pass the runId from `list_runs`.

## Testing

Vitest is the whole UI test story: store/dispatcher logic tested by feeding recorded
event sequences at the Pinia stores (`src/stores/__tests__`, `src/messages/__tests__`),
happy-dom for component mounts, CI gate in `.github/workflows/console-frontend.yml`
(path-filtered: node 22, `npm ci` → `vue-tsc -b` → `vitest run`).

End-to-end is **`src/Bobcat.Console.Specs/`** (issue #86, built 2026-08-21): the viewer's own
`Program` booted in-process over Alba's TestServer by a `MonitorHost` resource, and Bobcat's
Gherkin runner driving it as an MTP host that `dotnet test` collects. Four features — Live Runs,
Retries, Ejection, Exports — ingest events over `POST /api/ingest` exactly as a publisher does and
assert against `GET /api/runs`, `GET /api/runs/{id}` (added for this; same wire-contract status as
the list), and the export endpoints. That replaces a Playwright layer, deliberately: the whole
wire is verifiable without a browser, and the SignalR leg is pinned separately by
`relayToStore.test.ts`. Archives go to a temp `Monitor:DataPath` per run, never `~/.bobcat`;
`MonitorHost.Restart()` is how the hydration rules are exercised. The spec host publishes its own
progress like any other (a `dotnet bobcat` on 5525 sees the suite run while it tests a second,
in-memory viewer — no loop is possible, the instance under test has no address); CI sets
`BOBCAT_MONITOR=0`. What the framework was missing to write it is recorded on issue #62.

## Retention (built 2026-07-31)

The archive directory ages instead of growing forever. The NDJSON file's mtime is the aging
clock — every ingested event (heartbeats included) appends, so a file untouched for the whole
retention period has had a dead publisher exactly that long. A stale live archive is ejected
exactly like a manual eject (off the dashboard, into `ejected/`); a stale ejected archive is
deleted. Nothing is ever deleted straight out of the live folder, and a manual eject keeps its
data for the rest of the retention window. One knob: `Monitor:RetentionDays` config →
`BOBCAT_MONITOR_RETENTION_DAYS` env var → 14 days; zero or negative disables aging entirely.
Swept at boot (before rehydration, so a long-dead archive is never loaded just to be swept)
and hourly by `ArchiveRetentionService`.

## Hydration (built 2026-07-31)

Both directions are archive replays — one fold, two transports, nothing to keep in sync:

- **Monitor restart**: the registry replays every non-ejected NDJSON archive back into
  projections on boot. A rehydrated run with no terminal `RunFinished` is **orphaned** (its
  publisher is gone; rendering it "running" forever would lie) — any later event un-orphans
  it. Eject moves the archive to `ejected/` (never deletes) precisely so an eject survives
  restart. Torn tail lines (monitor killed mid-write) are skipped, not fatal.
- **Browser load/reconnect**: `useSignalR` calls `hydrateFromServer()` after connect and on
  every reconnect — `GET /api/runs`, then each run's NDJSON export replayed through
  `relayToStore`, i.e. the store's own live-event fold. Store handlers upsert (stepId guard)
  so replay over already-arrived live events cannot duplicate; local runs the server no
  longer lists are pruned.

Observed live: a supervisor test suite running in another checkout streamed dozens of
one-scenario SampleWorker runs, each its own dashboard card — each worker minted its own
RunId. Fixed 2026-07-31 by supervised-run grouping (see the Bobcat-side seams section):
verified live, the same SampleWorker suite across 4 worker processes is now exactly one
card — one `run_started` (`supervised`, total 7), all worker scenario/step streams, one
`run_finished`. Note the grouping only applies when the run is driven through a
`Supervisor` with `PublishToMonitor` on — workers launched by a supervisor that doesn't
publish keep the old one-card-per-worker behavior on purpose (a grouped run with no bracket
owner would render as an unnamed orphan).

## CTRF retryAttempts (built 2026-07-31)

`RunProjection` keeps every retried-away attempt's step history (`ScenarioProjection.
PriorAttempts`, snapshotted when `RetryScheduled` arrives — with the policy's disposition and
reason — or on the next attempt's start as fallback). The CTRF export renders the FULL attempt
list including the final attempt, matching the spec's own with-retries example; attempt objects
admit no extra members, so step detail and disposition/reason ride each attempt's `extra`.
Exports are validated against the official `ctrf-io/ctrf` schema — which also caught that
`suite` must be an ARRAY (hierarchy), fixed at the same time. Null-valued fields are omitted
(CTRF's typed fields don't admit null).

## Not built yet

- Gherkin-runner dogfood e2e against this UI (#86).
- **Elapsed-vs-expected per step.** Step progress (#99, Bobcat-side seams item 5) carries
  elapsed; "expected" needs a duration history across runs, which is the same committed
  ledger #44 layer 2 and #56 layer 3 want — one store, not three.
- **Supervisor-side test updates on the wire.** `ISupervisorObserver.TestUpdated` exposes a
  lane's live per-test state programmatically, but no monitor event carries it — a supervised
  run's step stream comes from each worker's own publisher, so the dashboard already sees
  more than the tap does. It earns a wire event when a non-Bobcat worker (xUnit, tUnit) is
  driven under the viewer; those publish nothing themselves.
