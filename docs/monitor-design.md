# What a run publishes — the monitor wire contract

Bobcat runs announce themselves. This is the publisher's side of that: the events a run emits,
the transport it emits them over, the seams that produce them, and the environment variables
that steer or silence the whole thing.

Decisions of record (2026-07-31), amended by the Bobcat/Stoat split (2026-08-09) and by the
console's move to Stoat (2026-09-18).

## The receiver is not in this repository

The console that receives these events — the run board, the archive, the exports, the MCP tools
over them, the Event Model canvas — **moved to [Stoat](https://github.com/JasperFx/stoat)**. Everything
in Bobcat that remembered across a process went with it; what stays here is the format and the
run. Bobcat has no web server, no store, no MCP surface and no frontend.

What did *not* move is the vocabulary a publisher speaks, and none of it is obsolete:

- **`Bobcat.Monitoring`**, `BobcatRunner.PublishToMonitor`, `MonitorPublisher`, the
  `Monitor:*` config keys, and the mirror records in `src/Bobcat/Monitoring/MonitorEvents.cs`.
- **`BOBCAT_MONITOR`, `BOBCAT_MONITOR_URL`, `BOBCAT_RUN_ID`, `BOBCAT_RUN_TAG`,
  `BOBCAT_RUN_OWNER`** — every one a user-facing contract.
- **Port 5525**, the address a publisher probes. It was deliberately kept rather than collapsed
  into Stoat's coordination port, because every publisher already probes it; Stoat's single host
  binds both.
- **`docs/monitor-design.md` keeps its filename.** Issues, handoffs and the CLAUDE.md seams all
  link to it by this name, and it documents the protocol at least as much as it ever documented
  the viewer.

Here "monitor" means *the thing a run publishes to*. Renaming any of it would be a breaking
change for consumers that have nothing to do with the split.

> [!NOTE]
> Sections below describe how the receiving side folds these events — `RunProjection`, the Pinia
> runs-store, `GET /api/runs/{id}`, the MCP `run_status` tool, the CTRF export. Those are Stoat's
> now. They are kept because **the fold is half of the contract**: a publisher that does not know
> how its events are read cannot tell an additive change from a breaking one.

## Transport: HTTP, fire-and-forget, never slows a run

Publishers (BobcatRunner, the supervisor, worker processes) POST batches of events to
`/api/ingest`. HTTP over raw TCP because the emitting client must be dependency-free
(`HttpClient` + STJ only — no Wolverine in Bobcat), and events arrive at tens/second, not
thousands. The invariant that outranks all others: **a test run is never slowed or failed by
the monitor.** Probe `GET /api/ping` once at startup with a tight timeout → publisher goes
no-op for the run if nothing answers; bounded channel, drop on backpressure; discovery via
`BOBCAT_MONITOR_URL` (default `http://localhost:5525`).

## Event model

`src/Bobcat/Monitoring/MonitorEvents.cs` — polymorphic `MonitorEvent` records. The STJ
type discriminator and the Wolverine message type name are pinned to the same snake_case
string, so ingestion JSON and the SignalR envelope agree by construction. Identity: `RunId`
(minted per run) + scenario uid `"{Feature}/{Scenario}"` — the string BobcatRunner,
`RetryBudget`, `SpecNodeMapping`, and `WorkPlan` already share. `RunStarted` carries the root
repository path + branch, the board's grouping key for parallel suites on one box.
`RunHeartbeat` exists so a crashed/orphaned run renders as such instead of "running" forever.

**There are two copies of these records and that is a decision, not drift** (issue #65). The
publisher's copy is the one above, in `Bobcat.Monitoring`; the receiver's lives in Stoat as
`src/Stoat.Console/Contracts/MonitorEvents.cs`. They stay unshared deliberately — Bobcat must
not depend on the console's Wolverine stack, and **the wire shape, not an assembly, is the
contract**. That is also what let a BSL console absorb an MIT viewer without either side
acquiring a reference to the other. Stoat's `ContractRoundTripTests` and
`ProgressContractRoundTripTests` are what keep the two copies honest, so a change made here
alone fails there; `Bobcat.Tests/Monitoring/FakeMonitorHost` is how this side is tested against
real HTTP without one.


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

7. **Foreign per-test progress: `test_started` / `test_finished`** (built 2026-09-01, issue
   #195). A supervised run of a **non-Bobcat** suite registered on the dashboard with the right
   total and then never moved — `scenariosFinished` stayed 0 for the whole run, observed live
   at 0/1627 for five minutes, which is nearly indistinguishable from a wedged run. The bracket
   was right; the gap was that per-scenario events come from each *worker's* own
   `MonitorPublishingObserver`, and a plain xUnit worker has none. The supervisor already had
   the facts (`ISupervisorObserver.TestUpdated`, item 5's tap) and simply was not forwarding
   them, so this is forwarding, not new machinery.
   - **A separate pair, not `scenario_started`/`scenario_finished`.** Those carry spec identity
     — `{Feature}/{Scenario}`, the string a design-time `SpecificationDescriptor` joins on —
     and feeding them an xUnit method uid would widen that meaning for every consumer of the
     join. `TestStarted`/`TestFinished` carry the *worker's* test id and say so; for a Bobcat
     worker the two strings coincide anyway. Nothing about spec semantics is implied: this is
     a progress bar, not #110's projection of foreign specs into the Bobcat model.
   - **The worker's own stream always wins.** The supervisor forwards for *every* worker,
     Bobcat ones included, because it cannot know which of them publishes without a marker only
     new workers would carry. `ScenarioProjection.WorkerPublished` (and the store's
     `ScenarioState.workerPublished`) is set by any `scenario_*`/`step_started` for a uid, and
     both new handlers stand down for it. The guard is a property of the *scenario*, so it
     holds in either arrival order — a forwarded verdict that lands first is overwritten by the
     worker's own, one that lands second is ignored — and one test is one card either way.
     Two extra events per test against a batching, backpressure-dropping publisher was the
     cheaper side of that trade.
   - **The framework's word travels verbatim.** `State` is Passed / Failed / Error / Skipped /
     Timeout / Cancelled, never re-labelled by the publisher: two enums meaning the same thing
     is how a vocabulary drifts. `ForeignTestOutcome.From` (and its Pinia mirror) does the
     mapping in one documented place per side — Skipped counts as a clean pass **because the
     supervisor's own `WorkerOutcome.Succeeded` does**, so the progress bar and the terminal
     `run_finished` counts cannot disagree about the same test; an unrecognised state is a
     failure rather than a drop, since not counting a finished test stalls the whole bar. The
     raw word survives on `ScenarioResult.State` for anything that wants the distinction.
   - **Indeterminate never reaches the wire.** Silence is not a verdict, and a padded outcome
     is not a live one — a test a crashed worker never answered for publishes no `test_finished`
     at all, so a crashed run cannot read as a complete one.
   - `DurationMs` is measured between the two updates on the supervisor's own clock, and is
     null when it never saw the start — unmeasured is never zero. `Lane` is null for a one-test
     isolated or recycled process, the same rule as `worker_faulted`. Discovery is never tapped.
   - Free consequence: a supervised xUnit run now has per-test rows in `GET /api/runs/{id}`
     and therefore a CTRF/JUnit eject, without a Bobcat reference anywhere in the suite.


## A repeated step is its own row (issue #322, built 2026-09-16)

`StepId` is a step **template** id — the step method's name — and was being used as a
within-scenario identity on both sides of the wire. It is not one, and the two step shapes the
grammar most recently encouraged both repeat: `Given {event} occurred` once per arranged event
(#259) and `And no events for {aggregate} "…"` once per re-pointed stream (#311, #320). So the
viewer under-reported exactly the arrangements the grammar recommends.

Two failures, one cause:

- **The projection left later occurrences running.** `StepFinished` resolved its step with
  `FirstOrDefault(s => s.StepId == e.StepId)`, so the first occurrence absorbed every finish. On a
  real 37-scenario suite: **33 of 182 steps stuck at `running`, across 21 scenarios, every one of
  them a `CleanPass`.** Nothing contradicted anything a reader was looking at, because a scenario's
  own outcome is computed elsewhere — which is why it went unnoticed.
- **The store collapsed them.** `handleStepStarted` upserted by `stepId`, so repeats became one row
  carrying the **last** occurrence's text and the **first** one's duration, welded together with
  nothing saying so.

Resolved without a protocol change, because the wire already carried enough:

- **Starts key on `stepNumber`**, which the publisher increments per scenario. That is
  occurrence-correct *and* stable across a replay — and hydration idempotency was the reason the
  old code keyed on `stepId` at all, so it had to survive.
- **Finishes and progress pair with the first occurrence still running.** `StepFinished` carries no
  `stepNumber`, but steps are appended in order and finish in order, so "first still running" is
  exact. Both the projection and the store use that same rule, and both keep a fallback so a late
  or replayed event lands somewhere rather than vanishing.

The alternative was adding an ordinal to `StepFinished` and `StepProgress`. Not needed, and a wire
change is a compatibility question where this is not.

Kept on the publisher's side of the record because **`stepNumber` is what makes it work**: it is
published from here, one per scenario, and any consumer that keys step identity on `stepId`
instead will collapse the same rows again.

## Not built yet

- **Elapsed-vs-expected per step.** Step progress (Bobcat-side seams item 5) carries elapsed;
  "expected" needs a duration history across runs, which is the same committed ledger #44 layer 2
  and #56 layer 3 want — one store, not three. See `design/ledger-design.md`.
- **Step result cells.** `label / expected / actual / comparison / verdict` on a step result, so a
  table step's failure travels as a marked-up table instead of the sentence it is flattened into
  today. That is issue #324, left out of #322 deliberately rather than bundled in.
- **Supervisor-side test updates beyond progress.** `ISupervisorObserver.TestUpdated` (item 5's
  tap) is supervisor-side only; item 7 forwards the part of it that a progress bar needs and no
  more.
