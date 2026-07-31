# Bobcat.Monitor — design notes

A deployable web console that shows live progress for every Bobcat test suite running on the
box. Primary purpose: visualizing AI-agent-driven test runs — much of Critter Stack development
is gated on testing time, and this makes that time observable. It grows toward two futures:
the Bobcat test *runner* tool (the working plan is that this UI tool eventually becomes
"Bobcat" proper and the base library gets renamed), and an AI agent progress/planning
visualization surface.

Decisions of record (2026-07-31):

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
- TS mirrors of the contracts are hand-written for now; lift CritterWatch's NJsonSchema
  codegen (records → `.ts` + `relayToStore` case insertion) when the contract count justifies.
- No Aspire. The Vite dev server proxies `/api` (ws included) to the host's fixed dev port
  5525. Bobcat will eventually need an Aspire *resource recipe* as a testing feature; that is
  unrelated to this tool's dev workflow.

Packaging (built 2026-07-31): `dotnet tool` (`ToolCommandName: bobcat-monitor`) with the Vite
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

`src/Bobcat.Monitor/Contracts/MonitorEvents.cs` — polymorphic `MonitorEvent` records. The STJ
type discriminator and the Wolverine message type name are pinned to the same snake_case
string, so ingestion JSON and the SignalR envelope agree by construction. Identity: `RunId`
(minted per run) + scenario uid `"{Feature}/{Scenario}"` — the string BobcatRunner,
`RetryBudget`, `SpecNodeMapping`, and `WorkPlan` already share. `RunStarted` carries the root
repository path + branch, the dashboard's grouping key for parallel suites on one box.
`RunHeartbeat` exists so a crashed/orphaned run renders as such instead of "running" forever.

The publisher client lives in Bobcat as `Bobcat.Monitoring` (issue #65): mirror records of
these contracts, DECIDED to stay deliberately unshared — Bobcat must not depend on the
monitor's Wolverine stack, and the wire shape (not an assembly) is the contract. The
round-trip tests in `Bobcat.Monitor.Tests` are what keep the two sides honest.

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
   future supervisor can group its workers' streams without supervisor changes.
3. **`ISupervisorObserver`** — NOT built, deliberately: the supervisor is to be left alone for
   now (decision 2026-07-31). When it happens: fire from `record(...)`, lane start/finish,
   recycle, worker faults; `MtpWorkerClient.handleNotification` already receives live per-test
   `testing/testUpdates/tests` updates and discards them — that's the tap. Meanwhile a
   supervised run still gets step-level visibility because each worker IS an MTP host running
   `BobcatRunner`, and its own publisher streams steps directly to the monitor.

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

`src/Bobcat.Monitor/Mcp/MonitorTools.cs`, mounted at `/api/mcp` — streamable HTTP, stateless,
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

Vitest is the whole UI test story for now: store/dispatcher logic tested by feeding recorded
event sequences at the Pinia stores (`src/stores/__tests__`, `src/messages/__tests__`),
happy-dom for component mounts, CI gate in `.github/workflows/monitor-frontend.yml`
(path-filtered: node 22, `npm ci` → `vue-tsc -b` → `vitest run`). End-to-end tests dogfood
the Bobcat Gherkin runner when it's ready — that replaces a Playwright layer, deliberately.

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
one-scenario SampleWorker runs, each its own dashboard card — each worker mints its own
RunId today. That is the known cost of leaving the supervisor untouched; the designed fix is
the supervisor setting `BOBCAT_RUN_ID` for its workers (plus dashboard grouping by
repository), when supervisor work opens up.

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

- TS codegen for the contract mirrors.
- Supervisor-side `BOBCAT_RUN_ID` grouping + `ISupervisorObserver` (when supervisor work
  opens up); Gherkin-runner dogfood e2e against this UI; the eventual rename (the tool
  becomes "Bobcat").
