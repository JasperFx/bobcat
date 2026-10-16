# The monitor wire: what a run publishes

What stays here is **the format and the run**, and none of its vocabulary is obsolete. Here
"monitor" means *the thing a run publishes to*, so renaming any of the following is a breaking
change for consumers that have nothing to do with the split — it needs its own decision, not a
sweep:

- The `Bobcat.Monitoring` namespace (`src/Bobcat/Monitoring/`), `Supervisor.PublishToMonitor`,
  `MonitorPublisher`, and the publisher's copy of the wire records in
  `src/Bobcat/Monitoring/MonitorEvents.cs`. **Two copies of those records is a decision, not drift**
  (issue #65) — the receiver's lives in `Bobcat.Ui/Contracts` (and Stoat keeps its own), and the
  wire shape, not an assembly, is the contract. That is what lets a BSL console read the same runs
  as the MIT one without either side acquiring a reference to the other. The cost is that an
  additive field is **invisible** to a receiver until it is changed to read it: `StepCell.Value`
  (issue #396) was read by nobody for a while, and nothing warned either party. `Bobcat.Ui`'s round-trip
  tests now catch that for the receiver in this repository; nothing catches it for Stoat's.
- `BOBCAT_MONITOR`, `BOBCAT_MONITOR_URL`, `BOBCAT_RUN_ID`, `BOBCAT_RUN_TAG`, `BOBCAT_RUN_OWNER`,
  `BOBCAT_RUN_COMMAND`, **`BOBCAT_RUN_COMMAND_FILE`** (issue #402 — a file whose contents are the
  CURRENT request's command id, which **wins** over `BOBCAT_RUN_COMMAND`; see the resident runner's
  warm suite for why a variable cannot serve), `BOBCAT_LIST_SPECS` (issue #391 — the path a suite
  writes its spec manifest to, and only when asked), `BOBCAT_RESIDENT` (issue #390 — the same
  request as `--resident`), `BOBCAT_RUNNER_ID` (issue #397 — the stable runner id a parent hands
  over, so the same checkout is the same runner across every relaunch), and the reserved `Monitor:*`
  configuration keys. **`BOBCAT_RUN_COMMAND` (issue #392) is `RunStarted.Command`** — the resident
  runner's command id, so a viewer can follow its own button press to the run it produced. Opaque
  like the tag, and deliberately independent of it: the tag says what work a run speaks for, the
  command says which request produced it, and a commanded run routinely carries both. It *is* a
  `BOBCAT_*` variable, unlike the session below, because here Bobcat is the thing asking — a
  commanded run is a child test host and the id travels down to it
  exactly as `BOBCAT_RUN_ID` does. **`CLAUDE_CODE_SESSION_ID` is read as well** (issue #389) —
  `RunStarted.Session`, opaque exactly like `Tag`, so a viewer can attach a run to the agent that
  ran it rather than inferring it from a working tree. Deliberately not a `BOBCAT_*` variable:
  Bobcat reads what an agent session already put in the environment instead of asking for it, so
  attribution needs no configuration. **It loses to the command (issue #401): a run carrying a
  `Command` reports no `Session` at all**, because a resident runner holds its launching agent's
  session for life and would otherwise claim every button press a person made. Every one is
  user-facing or on the wire. The `Monitor:*` keys that are *read* (`DataPath`,
  `RetentionDays`, `RetentionRuns`, `IdleMinutes`) belong to the console: they left with it in
  3ee3db9 and came back with `bobcat ui`, along with their `BOBCAT_MONITOR_DATA` /
  `BOBCAT_MONITOR_RETENTION_DAYS` / `BOBCAT_MONITOR_RETENTION_RUNS` / `BOBCAT_MONITOR_IDLE_MINUTES`
  spellings. Only `Bobcat.Ui` reads them, never a publisher.
- **Port 5525**, the *default* address a publisher probes (`MonitorPublisher.DefaultUrl`), and
  the port `bobcat ui` binds. Deliberately kept rather than collapsed into Stoat's coordination port,
  because every publisher already probes it.

**`BOBCAT_MONITOR_URL` is a LIST, and a run publishes to every target (bobcat#530).** Comma- or
semicolon-separated; `MonitorPublisher.ParseUrls` trims, drops empties and drops duplicates (keyed
on the `Uri` each entry resolves to, so a trailing slash is not a second target — a doubled target
would double every event). Two consumers now want the same events and only one can hold 5525:
`bobcat ui` (#524's command) keeps it and Stoat listens on its own, so it can show a gate's live progress
(`run_progress`, the lane events, `test_stalled`, `worker_faulted`, step progress) while a long
suite runs. **Forwarding from `bobcat ui` to Stoat was the alternative and was decided against** —
it would make Stoat's live view depend on `bobcat ui` running.

- **`MonitorTargets` is the fan-out, and the isolation is structural rather than promised.** It
  holds one `MonitorPublisher` per URL, and a publisher already owns its client, its bounded
  channel, its drop-on-backpressure policy and its give-up counter — so *"a run is never slowed or
  failed by the monitor"* holds **per target** with nothing added: an unreachable Stoat cannot cost
  `bobcat ui` one event, and the reverse. That is why the list is a composite over the existing
  publisher rather than N targets inside it; the thing the invariant is a property of is one target.
- **Probes run concurrently**, so N absent consoles cost one probe timeout rather than N. Nobody
  answering is the same "no publisher at all" as before (null, and the run proceeds), and
  `BOBCAT_MONITOR=0` still suppresses every probe.
- **The Event Model `PUT` goes to every target, each asked its own model-name question** — `GET
  /api/event-model` merges only the sources naming the name *that* console serves, so the decision
  is per console: one serving another model gets the half withheld with a notice **naming it**,
  while the others still receive it. `SpecEventModelPublisher.PublishAll(MonitorTargets, …)`
  publishes to the targets **concurrently** so the whole exchange stays inside one `Ceiling`, and
  each target's notices are collected and replayed on the calling thread — `notify` stays a
  single-threaded callback.
- **Taking commands stays singular.** `MonitorPublisher.ResolveUrl()` (first entry) survives for
  the one caller that needs one origin: `ResidentRunner`'s command stream. `bobcat resident --url`
  accepts the list, registers with the first entry, and passes the whole string down as
  `BOBCAT_MONITOR_URL` — commands come from somewhere in particular, a run may be watched from
  anywhere. Supervisor workers inherit the variable unchanged, so their per-scenario streams fan
  out exactly as the bracket does.

**The runner publishes the spec half of the Event Model** (issue #294,
`Monitoring/SpecEventModelPublisher.cs`). A model has two producers compiled into different
assemblies: the host's `event-model --url` export carries the slices but no `Specifications`,
while the generator's `BobcatEventModelSource` carries the spec identities a run outcome joins on
and is invisible to the host. **A push names its SOURCE and replaces only that source; `GET`
serves the merge** (issue #268) — latest-wins erased one producer every time. So a run that
attaches to a console PUTs its spec assemblies' generated descriptors under a source named for the
assembly, under the same never-slow-never-fail invariant as the event pump, and *refuses* when the
console is serving a differently-named model: `GET` merges only the sources naming the current
one, and publishing anyway would hide the other half rather than join it. Both halves of the merge
are keyed on the model name, which is why setting it explicitly is load-bearing rather than
cosmetic.

- **Every Event Model PUT says where its half came from (bobcat#532, stoat#92)**: optional `root`
  / `repository` / `branch` query parameters from `CheckoutProvenance` (`Monitoring/`), so Stoat can
  call a half from a different repository or checkout than the newest one stale. The run reads the
  working directory's checkout; `import-event-model --url` reads its `--out` directory's.
  **Named `CheckoutProvenance`, not `EventModelProvenance`** — JasperFx.Events already has a type of
  that name in `JasperFx.Events.EventModeling`, and a file that imports both stops compiling.
  Read from files, never a `git` process (`GitInfo.FindCheckout`: a worktree's config is in its
  `commondir`), and a field that can't be read is left off: a detached HEAD sends no `branch`
  (unlike `run_started`, which reports the short sha), because a sha compared as a branch would call
  a half stale that is not.

**A supervised suite that is not Bobcat's still gets live progress (issue #195).** Per-scenario
events come from each *worker's* own recorder, so a plain xUnit worker published
nothing and a supervised run's card sat at `0 / 1627` for its whole five minutes — the shape of a
wedged run. `SupervisorRunPublisher` forwards `ISupervisorObserver.TestUpdated` as **`test_started`
/ `test_finished`**, a deliberately separate, lower-fidelity pair: `scenario_finished.Uid` keeps
its documented meaning as spec identity (`{Feature}/{Scenario}`, what a `SpecificationDescriptor`
joins on), while these carry the *worker's* test id. No spec semantics are implied — projecting
foreign specs into the Bobcat model is #110, not this.

- **The supervisor forwards for every worker, Bobcat ones included**, because it cannot tell which
  of them publishes without a marker only new workers would carry. The gate that makes one test
  one card in either arrival order is a property of the *scenario* and lives on the receiving
  side (`Bobcat.Ui`, and Stoat's own copy) — see `docs/monitor-design.md`, where the receiving-side folds are
  kept deliberately, because a publisher that does not know how its events are read cannot tell an
  additive change from a breaking one.
- **`State` is the framework's own word** — Passed / Failed / Error / Skipped / Timeout /
  Cancelled — and travels verbatim as a string, never re-labelled by the publisher: two enums
  meaning the same thing is how a vocabulary drifts. **`Indeterminate` never reaches the wire**;
  silence is not a verdict, so a crashed run cannot read as a complete one. `DurationMs` is
  measured on the supervisor's own clock and is null when it never saw the start — unmeasured is
  never zero. `Lane` is null for a one-test isolated or recycled process, and discovery is never
  tapped.
- **`SpecId` carries the spec identity beside the uid (issue #515), and that is what makes the
  fold exact.** The receiving fold needs the two streams to name the same test, and in the
  projected lane they never did — `test_*.uid` is the MTP node uid (a hash) while `scenario_*`
  carries `{Feature}/{Scenario}` — so Stoat matched them by **method name**, ambiguous across
  classes and overloads. The identity is read from the suite's own `SpecManifest` at discovery
  through **`SpecUidJoin`**, which is `WarmProjectedResidentSuite`'s identity → uid lookup
  (#394/#470) extracted so both directions are one rule: two copies is how the end that runs a
  specification and the end that reports it would start naming different tests. Trailing and
  optional, so no wire version bump and no change for a reader that predates it. **Null is the
  honest answer** for a plain xUnit test, a suite that writes no manifest, a front-end whose
  discovery places nothing, and an identity more than one test claims (#489 — the manifest keeps
  one entry, so stamping it would name one of two tests that both are it, and `SpecFilterArguments`
  refuses such an identity for the same reason). `ReadWorkerRecoveryHints` is the gate, because it
  is the gate on reading the manifest at all; its name is kept because it is public API.

### A cell says exactly one thing on the wire (issue #396)

`StepCell` carries four content fields and a cell fills **one** of them: `Expected`/`Actual` for a
judged cell, `Note` for one that says a sentence (the set comparer's missing/extra/out-of-order
rows), and **`Value`** for one that was *not judged* — a decision table's input column.
`Bobcat.Monitoring.StepCells.From` is the single projection that decides. Before `Value`, a literal
reached a consumer as `{"name":"x","status":"ok","expected":null,"actual":null}` — there was
nowhere to put `CellResult.DisplayText` — and the column rendered empty in every grid while the
console showed it perfectly.

**`Value` is not a reuse of `Actual`, and that is load-bearing.** A cell with neither an expected
nor an actual is how `JsonRenderer` decides an input column earns no Status column (#384) — so
borrowing `Actual` for a value makes every input cell look judged, and broke that test when tried.
The plain-value `CellResult` constructor therefore stays the right one for an input cell; what was
missing was a field on the wire, not a different field in the model. `CellResult.DisplayText` also
gained a branch: a `failed` cell with no pair renders its `Note` rather than `expected '', got ''`,
which is what had kept the set comparer's out-of-order cell on the legacy constructor.
