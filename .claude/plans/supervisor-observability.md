# Supervisor Observability Plan

> **Status: SHIPPED, 2026-08-24 — all five PRs merged the same day the issues were filed.**
> #151 (pid, #146) · #152 (stall + heartbeat, #145/#148) · #153 (before-kill hook, #147) ·
> #154 (memory sampling, #149) · #155 (Snapshot, #150). All six issues closed. Supervisor
> suite grew 143 → 169 tests, still ~6s. Two things the build taught that the plan below did
> not know: the hook's live-wedge integration test proved an MTP host genuinely cannot exit
> while a test hangs (validating #147's premise), and #150 needed the ledger to keep
> **provisional verdicts** off the live stream — results are recorded per lane, so a mid-lane
> snapshot would otherwise have called nearly a whole single-lane batch indeterminate.
> The deferred list at the bottom still stands: stall→kill, `Run` returning partial results,
> and monitor wire events remain undone by decision.

**Issues #145–#150, filed 2026-08-24.** All six come from the same week of Wolverine CI
incidents (wolverine#4083, #4089, #4090, #4098, #4100) and share one diagnosis: the supervisor
knows facts about hung, heavy, and cancelled runs that consumers are currently reconstructing
badly from outside the process — shell watchdogs sampling `/proc`, guessing which pid is the
test host, and losing everything when GitHub discards a cancelled job's logs.

The cluster is one theme — **surface what the supervisor already knows** — and every piece
follows the codebase's established instincts: opt-in and off by default (like retries and
parallelism), "report, don't act" (like `RunTiming`), observers that can never change what a
run does, and unmeasured never zero-filled.

## Dependency graph

```
#146 pid ──────────────┬──> #147 before-kill hook
                       └──> #149 memory sampling
in-flight ledger (new) ─┬─> #145 stall detection
                        ├─> #148 heartbeat
                        ├─> #149 per-test attribution
                        └─> #150 snapshot (in-flight → Indeterminate)
#150 also needs the attempts history behind a lock
```

The **in-flight ledger** is the shared bookkeeping the issues themselves point at: since
#99/#119 the supervisor consumes `testing/testUpdates/tests` (`TestUpdated` observer callback,
fired from worker I/O threads), so "which uids are in flight, since when, in which launch" is a
thread-safe table over a stream already flowing. #145, #148, #149, and #150 are four surfaces
over that one table.

## PR sequence

Five PRs, ordered so each lands something usable and the shared plumbing is proven by the first
feature that uses it. Branch each off `main`.

### PR 1 — #146: expose the worker's ProcessId (small)

The enabler. `MtpWorkerClient` already holds `private readonly Process _process` and uses it in
`tryKill` and `describeFault`; nothing surfaces the id.

- `IWorkerClient` gains `int? ProcessId => null` (default interface member, same pattern as
  `OnTestUpdate`). `MtpWorkerClient` returns `_process.Id`. Nullable because an in-process
  client has no pid.
- **Surfacing to observers:** `WorkerLaunchContext` gains `int? ProcessId { get; init; }`,
  stamped by `Supervisor.launchWorker` after `_factory.Launch` returns and before the
  `OnTestUpdate` subscription captures the context — so every `TestUpdated` already carries the
  pid with no observer signature change.
- New default no-op observer member `WorkerStarted(WorkerLaunchContext worker)` so a consumer
  can correlate lane → pid before the first test update arrives (and for discovery workers,
  which never fire `TestUpdated`).
- `WorkerFault` gains `int? ProcessId` so a fault names the process that died.
- Tests: `FakeWorkerFactory` workers default to null; an `MtpWorkerClient` integration test
  asserts a real pid; an observer test asserts `TestUpdated`'s context carries it.

### PR 2 — in-flight ledger + #148 heartbeat + #145 stall reporting (the heart)

These two issues are explicitly "same underlying bookkeeping, different surface", so they ship
together with the ledger they share. **The name of the hung test is the missing thing** in
wolverine#4083 — this PR is where it stops being missing.

- **`InFlightLedger`** (internal to the supervisor): uid → (display name, launch context,
  started-at timestamp), plus completed/total counters. Written from the existing per-worker
  `OnTestUpdate` subscription in `launchWorker` (I/O threads — lock it), read from a timer.
  In-progress adds; terminal update removes and increments completed.
- **Time is injected.** Take a `TimeProvider` (default `TimeProvider.System`) on the
  supervisor so stall and heartbeat tests use a fake clock instead of sleeping. Timestamps via
  `timeProvider.GetTimestamp()`/`GetElapsedTime` — monotonic, like the existing launch timing.
- **One run ticker, two consumers.** A single `Timer` (or periodic task) alive only while
  `run()` is in flight, checking heartbeat and stall cadences independently. Same
  fire-after-stop care `SupervisorRunPublisher.stopHeartbeat` already takes.
- **#148 heartbeat:** `public TimeSpan? HeartbeatInterval { get; set; }` — null (off) by
  default. Emits through the existing `Log` callback, one line regardless of lane count:

  ```
  [bobcat] 4m12s — 143/275 done, 1 in flight (lane 0), longest running: MartenTests.Bugs.Bug_2318… (94s)
  ```

  "Longest running" is the valuable clause and is a ledger read. Also fan out as a default
  no-op observer member (`Heartbeat(...)` with the structured facts) so a dashboard gets it
  without parsing the log line.
- **#145 stall reporting:** `public TimeSpan? StallThreshold { get; set; }` (off by default)
  and `public Func<WorkerTest, TimeSpan>? StallThresholdFor { get; set; }` for per-test
  budgets by trait. New default no-op observer member:

  ```csharp
  void TestStalled(WorkerLaunchContext worker, string uid, string displayName, TimeSpan inFlight) { }
  ```

  Fired **once** per test per attempt when it crosses its threshold (not every tick — the
  heartbeat's climbing "longest running" figure is the continuous view). Also logged, and
  collected on `SupervisorResults.StalledTests` (uid, display name, in-flight duration at
  detection) so the fact survives into the report and #150's snapshot.
- **Reporting only.** Whether the supervisor should then kill the worker and record `Timeout`
  is the separable second decision the issue itself flags — deferred (see "Deferred" below).
  A stall threshold that auto-kills will eventually fire on a legitimately slow integration
  test; `RunTiming`'s "report, don't act" note is the right instinct.
- Tests: a `FakeWorkerFactory` worker that reports in-progress and then parks until released;
  fake clock advances past the threshold; assert `TestStalled` names the right test once,
  heartbeat lines carry done/total and the longest-running clause, and nothing fires when the
  knobs are null.

### PR 3 — #147: before-kill diagnostic hook (small)

A seam, not a feature — Bobcat ships no dump logic and takes no `dotnet-dump` dependency. The
consumer captures what it wants (`dotnet-dump collect` + `dumpasync --coalesce` is the artifact
that diagnosed wolverine#4100); the supervisor offers the moment, the pid, and a deadline.

- The kills live in `MtpWorkerClient` (`DisposeAsync`, the connect-failure path), so the hook
  belongs on **`MtpWorkerFactory`**, threaded into each client it launches:

  ```csharp
  /// Invoked with a live worker immediately before it is killed. Bounded; an exception
  /// or overrun never changes what the run does.
  public Func<WorkerKillContext, Task>? OnBeforeKill { get; set; }
  public TimeSpan BeforeKillTimeout { get; set; } = TimeSpan.FromSeconds(30);

  public sealed record WorkerKillContext(int? ProcessId, int? Lane, WorkerPurpose Purpose, string Reason);
  ```

- Fires only when the process is still alive (`!HasExited`) — a crashed worker already has an
  exit code and stderr, which `WorkerFault` covers. `Reason` distinguishes disposal from a
  connect timeout (and, later, a stall kill). Consumers filter on it; a healthy end-of-run
  disposal is cheap to ignore, and the wedged-worker case *is* a disposal of a live process.
- Bounded invoke: `Task.WhenAny(hook(context), Task.Delay(BeforeKillTimeout))`; exceptions
  logged and swallowed; the kill proceeds regardless. This is the one observer-shaped callback
  the supervisor genuinely waits on, which is why the timeout is explicit and small.
- Tests: unit-test the bounded invoke (overrun, throw); integration test that disposing a
  client with a live worker fires the hook with the pid, and a crashed worker does not.

### PR 4 — #149: memory sampling and per-test attribution (medium)

`RunTiming` for RSS. The motivating case: a **green** Wolverine job growing 375 MB → 9334 MB
across 275 tests, finishing with 172 MB free — one added test class from an OOM-killed runner
with discarded logs.

- `public TimeSpan? ResourceSampleInterval { get; set; }` — off by default. The run ticker
  from PR 2 gains a third consumer.
- Sampling stays behind the worker seam: `IWorkerClient` gains
  `long? SampleWorkingSet() => null`; `MtpWorkerClient` returns `_process.WorkingSet64`
  (refreshed) — no pid round-trip, no new dependency, works on every platform.
- Collected per launch: peak RSS, first/last samples. Per test: RSS delta across its attempt,
  **attributed only when the ledger shows exactly one test in flight in that process for the
  whole window** — otherwise counted as unattributed, explicitly. With
  `MaxParallelWorkers > 1` each lane is still single-file, so attribution usually survives;
  overlap within one process is what voids it.
- Reported the way `RunTiming` is: a `RunResources.For(results)` computed view over raw
  figures stored on `SupervisorResults`, rendered by `RunReport.ToText`/`ToJson` as a
  `Memory` section — peak per worker, **top-10 tests by memory retained** (the killer report),
  unattributed and unmeasured counts. Null in JSON for anything unmeasured, never zero; the
  text report says the numbers are a floor.
- Reporting only, same note as `RunTiming`: a memory threshold turned into a build failure
  converts a useful signal into a flaky one.
- Tests: fake workers with scripted `SampleWorkingSet` sequences; assert peak, delta
  attribution, the overlap → unattributed rule, and null-not-zero in the JSON.

### PR 5 — #150: `Snapshot()` — partial results for a cancelled run (medium)

`Run(ct)` throws on cancellation today, so a capped CI job's run reports nothing — and GitHub
discards the cancelled job's logs, so nothing survives at all. The issue weighs two shapes and
prefers `Snapshot()` as the safer start; agreed — `Run`'s throwing contract is established, and
`Snapshot()` is independently useful for dashboards.

- Hoist the per-run recording state (`attempts`, the planned post-filter test list) from
  `run()` locals onto run-scoped state guarded by a lock. Recording is already deliberately
  serial, so the lock is uncontended in the normal path.
- ```csharp
  /// A consistent view of the run so far. Safe to call from any thread while Run is in flight.
  public SupervisorResults Snapshot();
  ```
  Contents: every recorded attempt as-is; every planned test with no verdict synthesized as
  `Indeterminate` — the state that already means "the supervisor asked and never heard" — with
  the ledger distinguishing "in flight when the snapshot was taken" from "never started" in
  the message; `Duration` so far; `StalledTests` so far. Plus `SupervisorResults.IsPartial`
  so a consumer's ledger writer can label it honestly.
- Document the consumer pattern the issue describes: wire the CI termination signal
  (GitHub Actions grants a real grace period — an observed 5 minutes) to
  `Snapshot()` → write the flakiness ledger. A SIGKILL still leaves nothing; that caveat goes
  in the doc comment.
- Tests: snapshot from another thread while a fake worker parks — assert recorded verdicts,
  in-flight-as-Indeterminate, and that the eventual `Run` result is unaffected; snapshot after
  cancellation; `IsPartial` set.

## Deferred, deliberately

- **Stall → automatic kill + `Timeout` verdict.** #145 flags it as a second, separable,
  opt-in decision. Once PRs 1–3 land, a consumer can already do better than a kill: get named
  stalls, capture a dump via `OnBeforeKill`, and end the run on its own terms. Revisit with
  evidence from Wolverine's adoption.
- **`Run(ct)` returning partial results with `Cancelled = true`.** A contract change;
  `Snapshot()` first, per the issue's own preference.
- **Monitor/console wire events** for stall, heartbeat, and memory (`TestStalled` et al. onto
  `MonitorEvents.cs` — both copies — plus TS regeneration and store handlers, per
  `docs/monitor-design.md`). Worth doing, scoped out of these PRs to keep each reviewable;
  the observer members are the seam it will hang off.

## Cross-cutting rules

- **Every knob is opt-in and off by default** — `HeartbeatInterval`, `StallThreshold`,
  `ResourceSampleInterval`, `OnBeforeKill` all null. An unconfigured run behaves exactly as
  today. Same reasoning as retries and `MaxParallelWorkers = 1`.
- **New observer members are default no-ops** and honor the existing contract: fired via
  `notify(...)`, an observer that throws is logged and stepped over, a callback never changes
  the run. The one exception is `OnBeforeKill`, which is why it is not an observer member and
  carries an explicit timeout.
- **Threading:** `TestUpdated` fires on worker I/O threads; the ledger and the attempts
  history take locks; timers tolerate firing after the run ends.
- **Docs:** update the Supervisor section of `CLAUDE.md` per PR (the observability additions,
  the report-don't-act stance, the snapshot contract), and `RunReport` docs for the Memory
  section.
- Naming per the repo convention: Pascal public, `_camel` private instance fields.

## Issue cross-reference note

The issue bodies reference "#2" and "#3" in a few places ("Related: #2 (expose the worker
pid), #3 (capture diagnostics…)") — those are #146 and #147; the drafts predate filing. Worth
a quick edit pass on the issues so future readers aren't sent to Bobcat's issues 2 and 3.
