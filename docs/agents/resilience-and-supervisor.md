# Resilience, recovery hints, the ledger and the supervisor

### Resilience (`src/Bobcat/Resilience/`)
The retry policy layer — `FailureLevel` promoted to a decision the supervisor acts on.
Framework-neutral by design, so the same engine drives xUnit, TUnit, NUnit and MSTest workers
(issue #41).

- **`Disposition`** — `Pass` / `FailAndContinue` / `RetryInProcess` / `RetryInFreshProcess` /
  `RetryAfterRecycle(resources…)` / `AbortRun`, each with a human-readable `Reason` that reaches
  the report.
- **`IFailurePolicy`** — `AttemptContext → Disposition?`. **Returning null abstains**, so narrow
  policies compose; `FailurePolicyChain` takes the first non-null and `DefaultFailurePolicy`
  always decides last. Register with `Supervisor.AddFailurePolicy(...)`.
- **`RetryBudget`** — two independent ceilings because they fail differently:
  `MaxAttemptsPerTest` stops one pathological test, `MaxRetriesPerRun` stops a pathological
  *environment*. Default is `RetryBudget.None`, so an unconfigured run behaves exactly as before.
- **`ResilienceTags`** — the trait vocabulary policies read: `Retry=N`, `Isolated=true`,
  `RecycleOnRetry=rabbit,kafka` (a test writes `[Trait("Retry", "2")]`). The #43 spike found traits
  are the only metadata channel that survives every front-end intact, which is why policy keys off
  **traits, not exception types** — tUnit erases exception types entirely on the MTP wire.

**Retries are opt-in.** A failure with no `Retry` trait is never retried, even with a budget
configured. Retrying by default would turn every genuine assertion failure into a slower one and
make flaky indistinguishable from broken.

**Honest reporting ships with it** — `RunOutcome` is `CleanPass` / `PassOnRetry` / `Failed` /
`Aborted`, and `PassOnRetry` is never folded into `CleanPass`. `SupervisorResults.PassedOnRetry`
is the run's flakiness ledger; a pass-on-retry still exits 0 but is reported separately. A
disposition that could not be honoured is recorded in `UnsupportedDispositions` rather than
silently downgraded, so a report never implies a retry that never happened.

#### Recovery hints — author-declared, per failure class (issue #44 layer 1)

A trait says *this test* is unreliable; a hint says **which failure** is unreliable and what fixes
it. Declared on the test class (or method, or assembly), so an assertion failure in the same test
is still reported as the bug it is:

```csharp
[ClearsOnRetry(typeof(TimeoutException), Because = "the broker is slow to warm up")]
[ClearsOnRecycle("rabbit,kafka", typeof(BrokerUnavailableException))]
[ClearsInFreshProcess(typeof(BadImageFormatException))]
[NeverRecovers(typeof(SomeDeterministicBug), Because = "this is a real bug")]
public class OrderSpecs;
```

- **The attributes and `DispositionKind` live in JasperFx, not Bobcat** — namespace
  `JasperFx.Testing`, from **JasperFx 2.37.0** (issue #63). Any suite already referencing JasperFx,
  directly or through Marten/Wolverine/Polecat, can annotate itself **without referencing Bobcat**.
  That mattered because `docs/parallel-ready-suites.md` Step 0 sells "a test project needs no
  reference to Bobcat", and shipping the attributes as Bobcat types would quietly have spent it.
  One enum rather than a Bobcat copy mapped at a seam: two enums meaning the same thing is how the
  vocabulary drifts, and the drift would stay invisible until a hint silently stopped matching.
  Everything that *acts* on a hint stays here — `RecoveryHint`, `RecoveryHintSet.Best`,
  `HintedFailurePolicy`, the scope rule.
  - Consequence worth knowing: **a project using `DispositionKind` must reference `JasperFx`
    directly.** The console project did not — before it moved to Stoat — and picked up 2.36.1
    transitively from `WolverineFx.Http`
    — Central Package Management only pins what a project actually references, so the central
    2.37.0 did not apply and it compiled against a JasperFx nobody chose.
  - `ClearsOnRecycle` parses its resource list independently of `ResilienceTags.ParseResources`,
    because JasperFx must not reference Bobcat. `ResourceParsingAgreementTests` pins the two
    together; without it they can diverge silently and a trait and a hint naming the same brokers
    would resolve to different names.
- **`HintedFailurePolicy` sits between the user's policies and `DefaultFailurePolicy`** — explicit
  code still wins, and any failure no hint describes falls through to the trait-driven default
  unchanged. A run with no hints behaves exactly as it did before.
- **A hint never widens the budget.** `RetryBudget.MaxAttemptsPerTest` is the operator's ceiling;
  an unconfigured run still retries nothing however many hints are declared. Knowledge of what
  recovers belongs to the test's author, how much time the run may spend belongs to whoever runs
  it, and conflating them would let a spec author escape the ceiling.
- **`NeverRecovers` is the counterweight.** Without it the only way to stop a broad `Retry=3`
  re-running a deterministic bug is to remove the trait, which also stops the retries that were
  pulling their weight.
- **`FailureSignature` is the matching key** — type *names*, not `Type`, because out of process a
  name is all there ever is. In process it holds the whole inheritance chain, so a hint on a base
  class matches a derived failure; from the MTP wire it holds one name, so a base-class hint
  abstains. That asymmetry is deliberate and degrades to "no hint applied" — never to a wrong
  retry. A worker that erases the type entirely (tUnit) simply matches nothing.
- **Hints cross the process boundary through the spec manifest, not as attributes (issue #469).**
  The supervisor drives the worker as a process and never loads its assembly. The worker can read
  the attributes — it is the process they live in — so it writes them into its `SpecManifest` as
  `SpecManifestHint`s (the declaration's own coordinates: assembly, class or method), and the
  supervisor joins those to the uids its discovery reported and adds them to
  `Supervisor.RecoveryHints` (`ReadWorkerRecoveryHints`, on by default). Hints registered by hand
  still work and come first. This is why `RecoveryHint` exists separately from
  `RecoveryHintAttribute` — it is also the shape the ledger's proposals take.
- **Scope beats type specificity.** Assembly < class < method, and the narrowest declaration wins
  even when it names a less specific type, so a class can override a run-wide default without
  knowing what that default names. Within one scope, the hint naming the exact type wins.
- **The hint travels on the `Disposition`**, not just inside its `Reason` prose, because the case
  most needing a report is a hint that *suppressed* a retry — otherwise a test with a `Retry` trait
  that failed once looks like its trait stopped working. Surfaced as `↯ recovery hint applied` in
  the console and as a `hint` object per attempt in `RunReport.ToJson`.

**Layer 2, the committed ledger, is built** (`src/Bobcat/Ledger/TestLedger.cs`, design of record
`design/ledger-design.md`) — one store serving #44's hint proposals, #142's duration trends, and
`Supervisor.KnownTestDurations` (the third consumer that made it infrastructure:
`ledger.KnownDurations()` feeds `WorkPlan`'s balancer from the first pass). The open questions were
settled by making the merge strategy the design: a grow-only set of per-(test, run) observations
plus a deterministic clock-free prune — `Record`/`Merge` commutative, associative, idempotent;
serialization canonical (byte-identical for the same observations, whoever folds) — so a git
conflict always resolves by load-both-sides → `Merge` → save, no artifacts needed. Failure classes
are keyed by type *name* (the `FailureSignature` rule); aging is newest `MaxRunsPerTest` per test
plus explicit `PruneTestsNotSeenSince(cutoff)` for deleted tests. Fed by
`SupervisorLedger.From(SupervisorResults, runId, at)` (every attempt survives). Nothing writes the
ledger implicitly — recording is the caller's line of code, and stall-induced entries (#173) never
feed hint evidence. The DECIDED fork stands, now in code: `ProposeHints()` emits attribute *text*
with `Because` evidence (plus the `[NeverRecovers]` counterweight, `minOccurrences` gating both) — a
human accepts by writing it into the code, and nothing reads proposals back into an
`IFailurePolicy`; a policy that silently learns "just retry this" is exactly how red gets laundered
into green with nobody deciding to.

### Supervisor (`src/Bobcat.Supervisor/`)
The out-of-process half of #41: runs a suite across **worker processes** and applies the
resilience policy at the only altitude that can act on it. `RetryInFreshProcess` and running an
`[Isolated]` test alone cannot be decided or performed by the thing running inside the process
that needs replacing.

```csharp
var supervisor = new Supervisor(new MtpWorkerFactory("path/to/MySpecs"))
{
    RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 }
};
var results = await supervisor.Run();   // results.ExitCode, results.PassedOnRetry, …
```

- **`IWorkerClient` is the seam.** Every wire detail — JSON-RPC framing, MTP node shapes,
  parameter names — lives behind it. That was the #43 spike's top-listed mitigation: MTP's server
  mode is undocumented and has moved between versions, so if it shifts (or we ever want the
  native-protocol fallback) it is one implementation, not a refactor.
- **No `Microsoft.Testing.Platform` dependency.** The supervisor speaks the wire protocol rather
  than hosting the platform. That is part of what lets it drive any MTP worker, Bobcat or not —
  `ForeignFrameworkTests` drives `Bobcat.Tests` (a plain xUnit v3 host) to prove it, not just
  assert it.
- **Scheduling:** isolated tests are identified from **discovery traits, before anything runs**,
  and each gets its own process. Everything else is spread across `MaxParallelWorkers` lanes (one
  by default). Discovery itself uses a throwaway worker so it neither inherits nor leaves state.

#### Parallel worker pool (`WorkPlan`)

`MaxParallelWorkers` defaults to **1**, so a run is sequential unless asked otherwise — parallelism
changes what a suite means, and turning it on by default would convert working suites into flaky
ones on upgrade. Same reasoning as retries being opt-in.

- **Partitioning is by test class, and that is a correctness rule, not a tuning knob.** Measured on
  Wolverine's `PersistenceTests`: splitting the same 78 tests across 4 workers *per test* failed
  1–4 non-deterministically, while splitting *per class* passed 78/78 at the same wall clock. A
  test class is the unit every framework's isolation contract is written against — fixtures,
  `IAsyncLifetime`, and any static state. The real example that made this concrete was a class
  whose setup read `var schemaName = "sqlserver" + ++count;` from a `static int`: split across four
  processes, each restarts at zero and all four collide. **Nothing about that is visible in the
  test list**, so the planner must never separate tests the author kept together.
- **`WorkPlan.ClassOf` derives the key from the display name** because that is the only structural
  naming signal every front-end supplies. It handles `Feature/Scenario` and the dotted
  `Namespace.Class.method`, stripping theory arguments first so `method(x: "a.b")` does not shatter
  into a partition per argument. `Supervisor.PartitionKey` overrides it when the real coupling is
  something else.
- **Balancing is longest-processing-time-first over whole partitions**, fed by
  `Supervisor.KnownTestDurations` (per-test, by uid). A first run has none and falls back to test
  count; tests missing from partial data are charged the **median** of what is known, so adding a
  test to a suite of 30-second integration tests is not costed at a nominal second.
- **The largest partition sets the floor.** Wall clock is the slowest lane, so no fleet size beats
  the slowest single class. Measured: 164s baseline → 73.2s at 4 workers (count-balanced) → 70.3s
  duration-balanced → **66.8s at 8 workers**. Doubling the fleet bought 3.5s because one 61-second
  test dominates. That is why issue #56 (find the hot spots) and this feature are the same
  bottleneck from two directions.
- **A same-process retry returns to the lane the test ran in**, not merely to some warm worker.
  A class's static state lives in the process that ran it, so retrying elsewhere would be a
  fresh-process retry wearing a same-process label.
- **Recording is serial and in lane order** even though lanes finish in whatever order the OS
  decides — a report must not depend on which worker won the race.
- **What it cannot protect against** is state shared *between* classes: a fixed port, or one
  database every class truncates. That needs per-worker environments
  (`MtpWorkerFactory.EnvironmentFor`), which is still unbuilt. `PersistenceTests` did not need it;
  running several *projects* concurrently will.
- **`GuardAgainstAnUnfilteredRun` is not optional.** MTP silently ignores an unrecognised subset
  parameter and runs the whole suite, which is indistinguishable from a filter matching
  everything. A retry that did that would fold unrelated failures into the attempt, so a
  mismatch is treated as a protocol fault rather than trusted.
- **A crashed worker yields `Indeterminate`, never "failed".** The spike measured 0-of-9 outcomes
  surviving a crash on some hosts, so silence is absence of evidence. Indeterminate maps to exit
  code **2**, not 1 — "we don't know what happened" is not an ordinary red build. Every
  indeterminate outcome carries the worker's **exit code and last standard error** (bounded to
  the final 20 lines) in its `ErrorMessage`, and the run collects them in
  `SupervisorResults.WorkerFaults`. An indeterminate count with no explanation is not a report.
  - **The test that was *running* when the worker died was the exception to that, until #445 tripped
    over it.** Nodes arrive at least twice — in-progress, then final — and `MtpWorkerClient`
    records the in-progress update, so such a test already HAS an outcome: `Indeterminate`, with a
    null `ErrorMessage`, because an in-progress node carries no `error.message`. `Complete`'s
    unreported-tests pass skipped it for having an entry, so the one test a reader most wants
    explained reached the report unexplained — and `SupervisorEndToEndTests` met it as a
    `NullReferenceException` inside a Shouldly predicate rather than as a failed assertion.
    `Complete` now stamps the fault onto any non-terminal outcome with nothing to say, and never
    over a verdict the worker did give. Found because #445 made the final node update bigger, which
    shifted a crash race that had always been able to land here.
- **`RetryAfterRecycle` recycles, then runs the test alone in a fresh process.** Resources are
  registered with `Supervisor.AddRecyclableResource` (an `IRecyclableResource`;
  `DockerComposeResource` is the shipped one). Reusing the
  shared worker would leave it connected to the broker we just discarded. Naming a resource
  nobody registered is reported as a wiring mistake, not silently retried without recycling. A
  recycle that *fails* aborts the run with `AbortReason` rather than throwing, so results
  gathered before the infrastructure broke survive.
- **`ISupervisorObserver` narrates the run live** (`Supervisor.AddObserver`, issue #84): every
  attempt with the verdict that followed it, scheduled retries, lane start/finish, recycles, worker
  faults. Default no-op members, and an observer that throws is logged and stepped over — a
  dashboard must not be able to fail a test run. A scheduled retry carries the **true attempt
  number**, which is the fact only the supervisor has: a worker counts from one on every run
  request. `WorkerFaulted` has a structured form (`WorkerFault`: description, exit code, stderr
  tail, lane or null for a one-test process, pid) whose default forwards to the string one, so
  either signature works for an observer. All of it reaches the monitor as wire events
  (`LaneStarted`/`LaneFinished`/`ResourceRecycled`/`WorkerFaulted`) via `SupervisorRunPublisher`;
  see `docs/monitor-design.md` Bobcat-side seams item 4 for what the dashboard folds from them.
- **The worker's pid is surfaced, never guessed at** (issue #146): `IWorkerClient.ProcessId`
  (default interface member, null for an in-process client), stamped onto
  `WorkerLaunchContext.ProcessId` by `launchWorker` so `TestUpdated` carries it, announced by
  the `WorkerStarted` observer callback (fired for **every** launch, discovery included — a
  discovery worker never reports progress but is still a process someone may need to diagnose),
  and riding on `WorkerRunResult.ProcessId` into `WorkerFault`. The enabler for pointing
  external diagnostics — a dump, a stack capture, an RSS sample — at the right process: the
  Wolverine watchdog that had to infer the pid from `/proc` latched onto `sqlservr` instead of
  the test host, which is the class of bug this removes (issues #147/#149 build on it).
- **Stall detection and the heartbeat are reporting, never action** (issues #145/#148, one PR
  because they are the same bookkeeping): `InFlightLedger` is the supervisor's live in-flight
  table — fed from the `TestUpdated` stream before observers are notified, read by one run
  ticker (`StallCheckPeriod` = 1s when stalls are configured). `StallThreshold` /
  `StallThresholdFor` (per-test, wins for discovered tests) fire the `TestStalled` observer
  callback and a `STALLED:` log line **once per attempt** — a retry resets the clock — and
  collect on `SupervisorResults.StalledTests`, which survives into `RunReport` even for a green
  run (same reasoning as `Quarantine`). `HeartbeatInterval` emits one `Log` line however many
  lanes are running plus `Heartbeat(SupervisorHeartbeat)`; the "longest running" clause is the
  point — a stuck run shows as that figure climbing before any threshold fires. All off by
  default; whether the supervisor then acts on a stall is the second, separable, opt-in
  decision — `StallAction`, issue #173, below. Time is injectable (`Supervisor.Time`, a
  `TimeProvider`) so `StallAndHeartbeatTests` drives a fake clock and holds "hung" workers on
  a `TaskCompletionSource` instead of sleeping.
- **Acting on a stall is `Supervisor.StallAction` (issue #173)** — default `Report` (#145's
  behaviour, unchanged; detection always still reports whatever the action). `KillAndRetry`:
  the ticker's `escalate` kills the stalled worker through the new `IWorkerClient.Kill(reason)`
  (on `MtpWorkerClient`: straight to the process-tree kill through #147's `OnBeforeKill` hook
  with the stall's reason — no polite exit, because asking a wedged process nicely is how a
  kill hangs; connection teardown stays with the later `DisposeAsync`). The killed worker's
  `Run` returns a fault with synthesized Indeterminate outcomes exactly like a crash, and
  `record()` intercepts them (`stallKilled` flag, consumed per lane / per solo worker): the
  stalled test gets `RetryInFreshProcess` once — a second stall on its solo retry is
  `FailAndContinue`, never retried forever — and **innocent batch-mates killed alongside it
  resume in their lane** (`RetryInProcess` → the same slot, necessarily a fresh process)
  instead of being reported Indeterminate for the supervisor's own act. `AbortRun` stops the
  whole run on the first stall; in `KillAndRetry`, `MaxStallKills` (default 3) is the run-wide
  ceiling after which the next stall aborts — repeated stalls across tests are the shape of
  dead infrastructure. Three ledger rules, all deliberate: stall-managed outcomes **never
  consult the policy or spend the `RetryBudget`** (the kill is the supervisor's own act; a
  wedge is not a flake); `SupervisorAttempt.StallInduced` keeps those attempts out of
  `PassedOnRetry`/`Quarantine` (via `TestReport.WasRetriedForFailure` — a pass whose only
  earlier attempt was stall-induced is a **clean pass**, the stall story lives on
  `SupervisorResults.StallKills` + `StalledTests`); and a stall kill is **not a
  `WorkerFault`** — the fault ledger is for deaths the supervisor didn't order. Observers get
  `StallKilled(StallKill)`; `RunReport` renders a "Workers killed to clear a stall" section
  and a `stallKills` JSON block. Not on the monitor wire as its own event yet — the stall
  (`test_stalled`) and the retry (with the stall reason) already travel; a dedicated
  `stall_killed` wire event is a follow-on if the dashboard wants to render kills distinctly.
- **`MtpWorkerFactory.OnBeforeKill` offers a live worker to the consumer before killing it**
  (issue #147) — a seam, not a feature: Bobcat ships no dump logic and takes no dotnet-dump
  dependency; the consumer captures what it wants (`dotnet-dump collect` + `dumpasync` is what
  diagnosed wolverine#4100) against the `WorkerKillContext` pid, under `BeforeKillTimeout`
  (default 30s). Fires **only for a live process about to be forcibly killed** — one that was
  asked to exit and did not (a wedged worker, after `DisposeAsync`'s 5s grace) or one that
  launched but never became usable; a healthy worker exits when asked and never reaches it, a
  crashed one already has `WorkerFault`. `MtpWorkerClient.InvokeBounded` wraps the hook in
  `Task.Run` so even a synchronously-blocking hook cannot hold the kill past the deadline —
  this is the one callback a run genuinely waits on, which is why the ceiling is explicit.
  `Bobcat.Supervisor.ProjectedWorker`'s `Basics/hangs when armed` (`BOBCAT_HANG`) is the reusable
  wedge.
- **`ResourceSampleInterval` is RunTiming for RSS** (issue #149): `IWorkerClient.SampleWorkingSet()`
  (default null; `MtpWorkerClient` reads `Process.WorkingSet64` — no new dependency, null once
  the process is gone), `MemorySampler` brackets every attempt with boundary samples off the
  test-update stream and adds interval peaks via the run ticker (which serves stalls, heartbeat
  and sampling from one timer at the min cadence). Per-attempt "retained" deltas are attributed
  **only when the attempt had its process to itself for its whole window** — a second test
  entering poisons everyone, including itself, and those attempts report a null delta
  (unattributed, counted) rather than a guess. Unmeasured produces no record at all, never
  zeroes; JSON figures are null. `RunResources.For(results)` is the reporting view (peak per
  worker, top-10 retainers) rendered by `RunReport`; raw figures live on
  `SupervisorResults.WorkerMemory`/`TestMemory`. Off by default, report-don't-act — same
  guardrail as `RunTiming`.
- **`Supervisor.Snapshot()` is what a cancelled run leaves behind** (issue #150). `Run(ct)`
  still throws on cancellation — that contract deliberately did not change — and a capped CI
  job is exactly that case, with GitHub discarding the cancelled job's logs on top. The
  consumer wires the CI termination signal (GitHub Actions sends SIGTERM with a real grace
  period) to `Snapshot()` and writes its flakiness ledger from the result, which is stamped
  `SupervisorResults.IsPartial` (`Summarize()` leads with `PARTIAL`, JSON carries `partial`).
  Three tiers of honesty for a test without a recorded verdict: a verdict **heard on the live
  update stream** whose lane never returned is kept (results are recorded per-lane, so a
  single-lane snapshot would otherwise call nearly the whole batch indeterminate —
  `InFlightLedger.ProvisionalVerdicts` fills the gap, minus the detail only a recorded attempt
  has); a test in flight is `Indeterminate` saying so with its duration and lane; a test never
  reached is `Indeterminate` saying that. Recording state sits behind `_recordGate` so a
  snapshot from another thread reads a consistent view; note the long-standing classification
  that Indeterminate tests also count in `Failed` still holds for snapshots.
- **The cluster's live surfaces are on the monitor wire** (2026-08-24): `worker_started`
  (never for discovery — it launches before the run bracket opens, and `run_started` stays the
  stream's first event), `test_stalled`, and `run_progress` (the #148 heartbeat, carrying peak
  worker RSS when sampling is on — distinct from `run_heartbeat`, the bare liveness ping).
  Posted by `SupervisorRunPublisher`; how the receiving side folds and renders them is
  `Bobcat.Ui`'s (and Stoat's, for its own copy), and kept in `docs/monitor-design.md` (Bobcat-side seams item 4) because the fold is half of
  the contract — a publisher that does not know how its events are read cannot tell an
  additive change from a breaking one.
- **Preflight runs once before any worker is launched** (`Supervisor.Preflight`, which also checks
  every recyclable resource). See the environment-check note below.
- **Reporting** lives in `RunReport.ToText` / `RunReport.ToJson`. `Quarantine` is every test that
  needed more than one attempt — membership is *"was retried"*, not *"eventually failed"*,
  because a green build is exactly when chronic flakiness would otherwise go unnoticed.
- **Where the run spent its time** is `RunTiming` (issue #56 layer 1) — pure computation over
  `SupervisorResults`, rendered by `RunReport` into a `Timing` section and a `timing` JSON block.
  It answers what a profiler cannot, because these are properties of the *run* rather than of a
  process: slowest-N with each test's **share of wall clock** (the percentage is what makes
  someone act — "one test is 35% of the run", not "60.9s"), parallel efficiency
  `sum(durations) / wall clock` (Wolverine's `PersistenceTests` measured **1.07x**, which is how
  we learned its collection fixtures serialize xUnit's in-process parallelism), retry-amplified
  cost, the price of isolation, and worker launch overhead.
  - **Report, don't act** — the same guardrail as #44's hints. Failing a build on a duration
    threshold turns a useful signal into a flaky one; whether a slow test is a bug or a genuinely
    slow integration test is a judgement, and this is the evidence for it.
  - **Unmeasured is never zero-filled.** A framework that erases durations on the wire (tUnit,
    same as exception types) yields `Unmeasured` counts and `null` JSON figures, and the text
    report says the numbers are a floor. Zero-filling would make every other figure quietly wrong.
  - Layer 3 (trend across runs) now rides the committed ledger — `TestLedger.Trends()`, see
    `design/ledger-design.md`. Layer 2 (sleep-shaped-duration heuristics) stays **not built**:
    the cross-run variance it needs exists now, but its false-positive question is open and
    `[SlowByDesign]` belongs in `JasperFx.Testing` per the #63 precedent.

`Bobcat.Supervisor.ProjectedWorker`'s specifications can *detect* whether they were isolated (a
static per-process counter), so the isolation tests prove isolation happened rather than merely
proving a process was launched. There is a control test showing the same test fails when batched.
It is an ordinary projected xUnit v3 suite (serial, declaration-ordered, traits as plain
`[Trait]`s).

#### Environment checks reuse JasperFx — there is no Bobcat `IEnvironmentCheck`

**Do not define one.** Issue #41's text sketched a new `IEnvironmentCheck` interface; that was
written before checking, and JasperFx already owns the concept. Note also that the *old Oakton*
`IEnvironmentCheck` interface no longer exists in JasperFx 2.x — what exists is:

- `JasperFx.Environment.EnvironmentCheckResults` — `RegisterSuccess` / `RegisterFailure` /
  `Succeeded()` / `Assert()`
- `JasperFx.Environment.EnvironmentChecker.ExecuteAllEnvironmentChecks(IServiceProvider, ct)`,
  which collects checks from `ISystemPart.AssertEnvironmentAsync`,
  `JasperFx.Resources.IStatefulResource.Check`, and Microsoft's `IHealthCheck`
- `EnvironmentCheckException`, `LambdaCheck`

So `Bobcat.Runtime.Preflight` is a **thin collector that returns `EnvironmentCheckResults`**, and
`ITestResource.Check(CancellationToken)` is a default interface method deliberately mirroring
`IStatefulResource.Check` — same verb, same "throw to fail" contract — so one resource satisfies
both without adapting. `Preflight.AddContainerChecks(...)` delegates straight to
`EnvironmentChecker`. A Critter Stack user must never have to write their checks twice.

#### Decided against: an `ITestHostLauncher`-style abstraction

**Do not add a separate launcher seam between the supervisor and the worker process.** Process
creation stays inside `MtpWorkerClient.Launch`, and `IWorkerFactory` remains the only injection
point. Revisit only if a concrete requirement below actually arrives.

The idea was to port VSTest's `ITestHostLauncher` — let a caller supply the process. Rejected
because:

- **Its main purpose does not apply.** VSTest's launcher exists chiefly so an IDE can start the
  host itself and attach a debugger. In MTP that is a *protocol* concern, not a launch one: the
  client advertises `capabilities.testing.debuggerProvider` during `initialize` and the host then
  sends `client/attachDebugger`. We currently pass `false`. Building a launcher to solve
  debugging would solve it in the wrong layer.
- **The real gap it would have carried is now fixed directly** — worker exit code and standard
  error are captured and reported (see above). That was the concrete defect; it did not need an
  abstraction.
- **`IWorkerFactory` already covers the seam we use.** `FakeWorkerFactory` in the tests proves it,
  and a second interface would be ceremony.

The remaining motivations are speculative: per-worker environment shaping (each isolated worker
pointed at its own broker/schema), and container or remote workers.

**Resolved — and the deferral paid off.** Per-worker environments became real the moment parallel
workers had to point at their own database. The requirement arrived far smaller than the
abstraction we would have built for it: `IWorkerFactory.Launch` gained a
`WorkerLaunchContext(Lane, Purpose)` parameter and `MtpWorkerFactory` an `EnvironmentFor` hook.
No launcher interface, no second seam. Had we written `IWorkerLauncher` up front we would have
shipped a whole abstraction where a parameter was wanted.

- `Lane` is bounded by `MaxParallelWorkers` and is the slot to key per-worker resources off — a
  database name, a schema prefix, a port. Discovery, isolated and recycled launches all report
  lane 0, because they never run while the pool is running, so the number of databases a suite
  provisions equals the workers it asked for rather than the processes it happened to start.
- `Purpose` distinguishes those cases when a caller wants to (a throwaway database for discovery).
- Per-worker environment layers **over** the factory's shared environment, and the lane's value
  wins — shared is a baseline, not a ceiling.

Container or remote workers remain speculative. Same rule applies: build it when something needs it.
