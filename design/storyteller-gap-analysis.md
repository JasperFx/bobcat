# Storyteller → Bobcat gap analysis

**Design record, started 2026-10-16.** A fresh, feature-by-feature comparison of Storyteller 5.4
(github.com/storyteller/Storyteller `master` — *not* `~/code/storyteller`, which is the abandoned v6
skeleton) against Bobcat as of `45888b8` (projected-only, post-0.50.0). Each Bobcat claim below was
checked against tracked source under `src/`, not against docs or memory.

**This is a living record.** Decisions made about any gap go in the [Discussion log](#discussion-log)
at the bottom, dated, with the gap id — and the gap's row is updated to point at it. Do not delete a
gap once decided; change its status. The point is that "why doesn't Bobcat do X the way Storyteller
did" is answered here once, not re-derived in every session.

Earlier passes this supersedes for the *comparison* (their decisions still stand):
the 2026-06-05 grammar gap analysis (#12–#18, all shipped), the 2026-09-28 sample projection
(`src/Bobcat.Xunit.Samples/README.md`), and #212 (grammar modules).

**Framing (Jeremy, 2026-10-16): Storyteller is an influence, not a migration source.** It has been
a dead project for years and has no real usage left. So there is **no migration audience**: no
Storyteller-to-Bobcat converter, no "coming from Storyteller" documentation, and no gap is worth
building *for parity's sake*. A gap earns a place in Bobcat only on its value to Bobcat's own users.
The ranking below was first written "by how much a migrating user would feel it"; read it as a rough
order of value instead.

Status vocabulary: **Covered** · **Exceeds** (Bobcat does more) · **Partial** · **Gap** ·
**Dropped** (a documented decision) · **Moved** (to Stoat, WolverineFx.Bobcat, or the test
framework) · **Moot** (existed for a UI or file format Bobcat doesn't have).

---

## 1. Verdict

The **authoring model is substantially covered and in places exceeds Storyteller**: typed
comparison with tolerance and non-equality comparisons, structural object comparison with
collections-as-sets, partial objects with constructor-aware building, compile-time table column
checks (BOBCAT032), declared defaults, `EventuallyAsync` reporting the last value. The Paragraph,
Curry, Import and Embed grammar kinds are all covered by plain C# composition.

The **real gaps cluster in four places**:

1. **Fail-fast semantics out of process** — a verified defect, not a design choice (G1).
2. **Cell input conversion is closed** — no custom converters, no `IParsable`/`ctor(string)`, no
   arrays on step parameters, invariant culture only (G3).
3. **Spec lifecycle** — no Acceptance-vs-Regression "runs, is reported, cannot fail the build" (G4).
4. **Persisted run artifacts and run-level resilience from the CLI** — the JSON report is never
   written, and retries/timeouts/stall kills exist only for a hand-written `Supervisor` host (G2, G5).

---

## 2. Gap register

Ranked by how much a team migrating a Storyteller suite would feel it. **Decision** says whether
the gap needs a design call (owed), is a plain bug/build task (none needed), or is already settled.

| Id | Gap | Storyteller | Bobcat today | Decision |
|---|---|---|---|---|
| **G1** | **A worker's catastrophic failure does not abort a supervised run; critical failures are retryable.** | `StorytellerCatastrophicException` stops the batch; `BatchExecutionMode.ShouldRetry` never retries after a critical exception | `Supervisor.failureLevelFor` maps any worker `Error`/`Timeout` to `FailureLevel.Critical` and never reads `outcome.ErrorType`; `DefaultFailurePolicy` aborts only on `FailureLevel.Catastrophic` or a CLR `SpecCatastrophicException`, neither of which exists out of process. The only abort test (`SupervisorTests.a_catastrophic_policy_decision_aborts_the_run`) uses a custom always-abort policy. Separately, `DefaultFailurePolicy` retries a Critical failure that carries a `Retry` trait, where `design/history/spec-driven-development-design.md` says Critical is "Retry: never". Under plain `dotnet test` nothing halts the suite. | **Decided 2026-10-16 — 1.0 blocker, see log.** |
| **G2** | **No persisted run artifact.** | `stresults.htm` (self-contained HTML), `--json`/`--dump`, `--csv` | `JsonRenderer.RenderScenario`/`RenderTiming` exist but nothing outside tests calls them. `ScenarioReportVisibility` documents `run --json --verbose`; `bobcat run` has neither flag. Without Stoat running, CI keeps console output plus `RunReport` verdicts only. | **Decided 2026-10-16 — 1.0, see log.** |
| **G3** | **Cell input conversion is closed.** | `CellHandling.Conversions` / `IConversionProvider`, `IRuntimeConverter` (look up/create via services: a "Player" cell loads a player), `ctor(string)`, comma arrays of T, `--culture`, "Wednesday 14:30" | `CellValues.Read` is a closed switch ending in `Convert.ChangeType`; no registration point, no `IParsable<T>`/`TypeConverter` arm, `InvariantCulture` hard-wired, arrays only inside partial objects/`BuildRows` members. The *output* side is open (`CellCheck.Register<T>(IValueChecker<T>)`). A strong-typed id "has no table-cell form" (CLAUDE.md, #512). | **Decided 2026-10-16 — 1.0, see log.** |
| **G4** | **No Acceptance-vs-Regression lifecycle.** | Acceptance specs run, report, never retry, never fail the build; `--lifecycle` filter | `[BobcatSpec(Pending = true)]` *skips* (#404, deliberately: "skipped, never swallowed"). Nothing runs-but-cannot-fail. | **Decided 2026-10-16 — 1.0, see log.** |
| **G5** | **Resilience is library-only.** | `--max-attempts`, per-spec `MaxRetries`, default 60 s spec timeout, `--global-timeout` | `RetryBudget`, hints, isolation, `StallThreshold`/`StallAction` all exceed Storyteller — but need a hand-written `Supervisor` host. `bobcat run` and `dotnet test` never retry, and there is no default per-spec timeout (framework `Timeout` only). | **Decided 2026-10-16 — 1.0, see log.** |
| **G6** | **An exception ends the scenario.** | Engine records the step error and keeps executing (unless critical) | Any exception ends the test; later steps never judged (`docs/marker-steps.md` "honest limits"; Samples README gap 1) | **Dropped — reconfirmed 2026-10-16, see log.** |
| **G7** | **Selection lists beyond enums.** | `[SelectionValues]`, `[SelectionList]`, fixture/system/dynamic lists, string cells validated against a list | Enum parameters only (a bad cell names the alternatives) | **Dropped 2026-10-16, see log.** |
| **G8** | **Value-check sentences and sentence `out` parameters are hand-written.** | `ValueCheckMethod`: a sentence's return value / `out` params compared to the step's expected text automatically | Only `TableRunner` compares returns and `out`s; a sentence uses `SpecAssert.Check` by hand | **Dropped 2026-10-16, see log.** |
| **G9** | **No step-level logging; no custom timing records; no log capture.** | `Reporting.Log(...)`, `DebugReport` (Trace/Debug capture), `Timings.Record(type, subject)` | `SpecReport` is scenario-scoped tables (#408); `SpecOutput.Write` goes to runner output only and nothing in `src/` calls it except adapters' reports; `RecordedStep` carries no logs (Samples README gap 3); no `ILoggerProvider` routing app logs | **Decided 2026-10-16 — (a) 1.0, see log.** |
| **G10** | **Fixture SetUp/TearDown are invisible to the spec.** | `Fixture.SetUp/TearDown` timed, failures rendered as the spec's result | Constructor / `IAsyncLifetime` / framework fixtures; not rendered, `LifecycleMeasured = false` | **Folded into G1 (1.0); rest dropped — 2026-10-16, see log.** |
| **G11** | **Relational database grammars.** | `Storyteller.RDBMS`: `DatabaseFixture`, stored procs, row verification, Postgres/SQL Server | Critter Stack store vocabulary only | **Dropped 2026-10-16, see log.** |
| **G12** | **No `BreakOnWrongs` / `BreakOnExceptions` switches.** | `StopConditions` per run | Wrongs always gather; exceptions always stop | **Deferred until asked for — 2026-10-16, see log.** |
| **G13** | **Smaller authoring gaps.** | — | — | **Triaged 2026-10-16, see log.** Mostly none needed: <br>• `VerifyPropertiesOf` checking a no-arg method or a field (`PropertyCells` reads properties only)<br>• per-set column headers/defaults (`[Header]` is global to the property)<br>• an `ApiFixture<TIn,TOut>`-style model-in/model-out helper<br>• JSON-text comparison (`CheckJsonGrammar`) and CSV grammars<br>• a spec with no steps still renders OK (Samples README open question)<br>• conversion failure is a row error, not an `invalid` cell; BOBCAT030 (literal arguments at build time) retired with Gherkin and nothing replaced it |
| **G14** | **Smaller tooling gaps.** | — | — | **Triaged 2026-10-16, see log.** • profiles / `--prop` (env + `IConfiguration` instead)<br>• CSV performance output<br>• `dump-fixtures`: a vocabulary catalogue an agent or analyst could read (`preview` renders specs, not grammars)<br>• living docs: embedding rendered spec results in documentation (`dotnet-stdocs`); `PlainSpecText`/`preview` could feed it<br>• ~~no Storyteller → Bobcat migration converter~~ — not wanted, see framing<br>• `SpecRender.Timeline` / `JsonTimelinePointOutput` are dead since the Gherkin lane went (nothing populates them) — delete or repopulate |
| **G15** | **Other ecosystem integrations.** | Selenium (`ScreenFixture`), Redux, TestRail sync, AspNetCore `HttpRequestReport` + request perf threshold | none in this repo | **Dropped 2026-10-16, see log.** |

---

## 3. Deliberately dropped (do not re-open without new information)

| Storyteller feature | Why Bobcat doesn't have it | Where decided |
|---|---|---|
| `ISystem` / `SimpleSystem` / `IExecutionContext`, warmup | Hosting is the test runner's job (fixtures, `IAsyncLifetime`) | `docs/integrations/index.md` |
| `[PerfLimit]`, `PerformancePolicies` | "Report, don't act": a duration threshold turns a useful signal into a flaky one | CLAUDE.md, `RunTiming` |
| Continue past an exception | Irreducible spec-engine vs test-method difference | Samples README gap 1; `docs/marker-steps.md` |
| Paragraph `.AsTable()` (a row of several grammars) | Paragraphs out of scope for tables | `docs/tutorials/data-intensive-specifications.md` |
| Markdown spec files, `convert`, `st open` editor, spec editing, `[Editor]`, step-through | Code-first only; Gherkin lane removed in 0.50.0; the IDE debugger steps through a test; the UI is Stoat's | CLAUDE.md; bobcat#471 |
| Run queue | The monitor owns the queue; a runner refuses rather than queues | CLAUDE.md, resident runner |
| Warmup / lifecycle timing | Lifecycle belongs to the runner; "not measured", never zero | #141 |
| Selection lists (`[SelectionValues]`, fixture/system/dynamic lists) | A closed set of values is an enum in C#, and enum cells already fail naming the alternatives; the editor drop-down they fed is gone | G7, 2026-10-16 |
| RDBMS grammars, Selenium, Redux, TestRail | Query with EF Core/Dapper/Marten and verify with `ObjectSetVerification`; Playwright specs are plain tests; TestRail would be a third-party plugin on existing seams | G11/G15, 2026-10-16 |
| Bogus/AutoBogus fill values | Random values make specs unreadable and failures unreproducible | #421 (decided 2026-10-08) |

## 4. Where Bobcat exceeds Storyteller

Typed comparison with `CheckOptions` tolerance and the closed `Comparison` enum (non-equality cells);
structural `ObjectComparison` with collection members as sets and dictionaries keyed (#456); partial
objects, `Specify<T>()`, constructor-aware `ObjectConstruction`, F# shapes; `DeclaredDefaults`
(#421 option 2, in `src/Bobcat/Partial/DeclaredDefaults.cs`); BOBCAT032 compile-time table column
checks; `EventuallyAsync` reporting the last value; NOW and finer relative time tokens over
`BobcatClock`; Kuhn-matched set verification with ORDER only when unavoidable; retries with budgets,
recovery hints, fresh-process / recycle dispositions, isolation, parallel lanes, stall detection and
kill, RSS sampling, the committed ledger; spec identities and run-by-identity; the resident runner;
compile-time diagnostics in place of `validate`; Event Model binding (no Storyteller analogue).

## 5. Storyteller samples with no counterpart in `Bobcat.Xunit.Samples`

From `src/Samples/Specs`: Arrays (3), Cells (2), Checking Json (4), Create Objects (5 construction
docs), Csv (2), Exception Formatting/Custom_Formatting, Facts/Throw_Critical, the `SetupBlowup`
fixture, Glossary (6), Performance_Thresholds, Start_with_Zero, Models (ApiFixture, ModelFixture),
Fancy_Paragraph, Sets (non-primitive type, enum), Tables (Before_and_After_Actions, Decision_Table,
`[ExposeAsTable]`, Using_a_Paragraph), Calculator (Flow_vs_Table, Adding_and_Subtracting,
Assertions).

From `src/StoryTeller.Samples/Specs`: Embedded/Embeds; General (Occasionally_Times_Out, Selection
List Values, Services, System_Defined_Lists_and_Runtime_Converters, bad grammar / missing fixture —
both now compile errors); Paragraphs (Simple Composite, Composite with Errors); Sentences/Importing
and Currying; Sets (SetWithError, ordered/unordered success and failure); Tables (Boolean Results,
ParkingExpiry decision table with enum + `AliasAs`, Tables with Errors).

The ones worth recreating, because they exercise a *rendering* Bobcat claims to have: Throw_Critical,
Tables with Errors, SetWithError, Boolean Results in a Table, Set with enum, Composite with Errors.

## 6. Documentation drift found during this pass

- `src/Bobcat.Xunit.Samples/README.md` still describes a `[SetVerification]` attribute and
  BOBCAT014/BOBCAT031; the attribute is gone and BOBCAT031 retired with Gherkin.
- `docs/tutorials/data-intensive-specifications.md` says BOBCAT030 checks literal arguments at build
  time; BOBCAT030 is retired and nothing emits it.
- `src/Bobcat.Xunit.Samples/Grammars/ObjectsGrammar.cs`, `SetsGrammar.cs` say "On a Fixture it is
  `VerifyObject(...)`" — there is no `Fixture` class.
- `ScenarioReportVisibility` and CLAUDE.md document `run --json --verbose`; the flags don't exist (G2).
- CLAUDE.md calls the unspecified-values policy "an open design question (bobcat#421)"; #421 was
  decided 2026-10-08 and option 2 is built.

---

## Discussion log

Append-only. One entry per discussion: date, gap ids, who decided, what, and why. Update the gap's
**Decision** cell to "Decided YYYY-MM-DD — see log".

### 2026-10-16 — comparison written

- Comparison produced (two parallel passes: authoring, runtime/tooling), Bobcat side verified by
  grep against `src/`. G1's out-of-process half verified by reading `Supervisor.failureLevelFor` /
  `exceptionFor` and `DefaultFailurePolicy.Decide`; **not** reproduced by a run.
- No decisions taken yet. Owed: G1 (critical retryable?), G2, G3 (converter registration), G4, G5,
  G7, G8, G9, G11, G12.

### 2026-10-16 — G1, fail-fast semantics (Jeremy) — **1.0**

- **Bug, fix for 1.0:** the Supervisor maps a worker outcome whose `ErrorType` names
  `SpecCatastrophicException` to `FailureLevel.Catastrophic`, so `DefaultFailurePolicy` aborts the
  run. Where a framework erases the type (TUnit) it degrades to today's behaviour; document that.
- **Critical stays retryable under a `Retry` trait.** The code is right and
  `design/history/spec-driven-development-design.md`'s "Retry: never" is wrong. Reason: in an
  integration suite, the flakiness a `Retry` trait exists for mostly arrives as exceptions
  (timeouts, broker not ready, connection refused), all Critical. `[NeverRecovers(typeof(X))]` is the
  per-type opt-out.
- **In-process halt, for 1.0:** once a scenario throws `SpecCatastrophicException`, every later
  scenario opening in that process fails immediately, naming the spec where the environment was
  lost. Plain `dotnet test` then reports one cause fast instead of hundreds of timeouts.
- **Documentation must make a point of it.** Canonical example: a Playwright suite whose application
  cannot start at all. There is no point running the rest; fail the suite and report the
  catastrophic failure fast.

### 2026-10-16 — G2, persisted run artifacts (Jeremy) — **1.0**

- **Both a JSON results file and a self-contained HTML results page ship in 1.0.** Storyteller
  parity (`stresults.htm`) on day one.
- Shape agreed in discussion: the JSON is the source (every scenario's `SpecRender` for the run,
  through `JsonRenderer`); the HTML is rendered from that same model, one file, no server, inline
  assets only, so CI can archive it as a build artifact. It is an archived file, not a live console;
  live stays Stoat's.
- **The HTML report is opt-in, never written by default** (Jeremy, same day). JSON is the
  cheap default-capable artifact; HTML is asked for with a flag.
- **The HTML page is built with Vue.js, from the package** rather than hand-written HTML — the
  same component family as the `bobcat ui` SPA (#524) and `@jasperfx/event-model-vue`, so the
  archived page and the live board render a scenario the same way. "Self-contained" then means the
  built bundle plus the run's JSON inlined into one file (e.g. a single-file Vite build), still with
  no server.
- The documented-but-missing `bobcat run --json --verbose` becomes real as part of this rather than
  being deleted. Exact switches (a `bobcat run` flag and a `BOBCAT_*` variable so plain
  `dotnet test` can opt in) settled at implementation; the variable must go in ai-skills.

### 2026-10-16 — G3, cell input conversion (Jeremy) — **1.0**

- **Convention plus registration ("option 2") for 1.0.** `CellValues.Read` falls back, in order, to
  `IParsable<T>`, a `TypeConverter`, then a public `ctor(string)`; and a global registration point
  (`CellValues.Register<T>(Func<string, T>)`, the input-side mirror of `CellCheck.Register`) covers
  types the suite does not own. A registration wins over the conventions.
- **Comma-delimited arrays and lists, definitely 1.0** — on step parameters and row-method
  parameters, not only inside partial objects. "That was valuable" in Storyteller. Each element goes
  through the same conversion (so a list of strong-typed ids works).
- **Service-backed converters (`IRuntimeConverter`) deferred, not dropped.** Possibly valuable later
  for building test data quicker (a cell names something to load or create); wait and see whether
  real suites ask for it. Core has no service locator, so if it comes it likely hangs off a scenario
  that owns a host (`CritterStackScenario`).
- Culture setting: post-1.0, only on demand (invariant culture is deterministic). Day-of-week dates
  ("Wednesday 14:30"): dropped; `TODAY+N` covers it.

### 2026-10-16 — G4, Acceptance vs Regression lifecycle (Jeremy) — **1.0**

- **`[BobcatSpec(Lifecycle = Lifecycle.Acceptance)]`**, `Regression` the default.
- An Acceptance spec **runs every time and is never retried.** On failure the platform reports it
  **skipped**, with the reason `acceptance spec, not yet passing: <first failure>`, through each
  framework's run-time skip (xUnit v3 `Assert.Skip`, TUnit `Skip.Test`, NUnit `Assert.Ignore`,
  MSTest `Assert.Inconclusive`), so the build stays green.
- **Bobcat's own surfaces carry a distinct verdict, `acceptance: failing`**, with full cells and
  steps: console, JSON, HTML, the monitor wire. That is what separates this from the
  run-and-absorb #404 rejected: nothing is swallowed, the failure is recorded under its own name.
- A passing Acceptance spec reports **"ready to promote to Regression"**.
- **`BOBCAT_LIFECYCLE=strict`** makes Acceptance failures real failures (a branch claiming done).
- **`Pending = true` stays** for a stub that cannot run at all.
- Why it matters more than in Storyteller: the spec-first agent loop writes specs red before the
  code exists; Pending hid that progress, Acceptance shows it slice by slice without failing CI.
- Known limit: in an IDE or TRX a failing Acceptance spec looks like any skip; only the reason text
  distinguishes it. The wire needs a new verdict value — a Stoat contract change to coordinate.

### 2026-10-16 — G5, resilience from the command line (Jeremy) — **1.0**

- **`bobcat run` exposes the Supervisor.** Flags: `--retries N` (the `RetryBudget` per-test
  ceiling), `--workers N` (lanes, partitioned by class), `--stall <duration>` with
  `--stall-action report|kill-and-retry|abort`, and `--results <dir>` (G2's artifacts). Any of them
  makes the run supervised; a plain `bobcat run` is unchanged.
- Retries stay opt-in per test: `--retries` sets the ceiling, a test still needs its `Retry` trait
  or a recovery hint. Same reasoning as the original "retries are opt-in".
- **No default per-spec timeout.** Hard timeouts belong to the test framework; a default kill is a
  flaky-build generator for slow integration suites. Instead a supervised run **reports** (never
  acts on) a stall after **5 minutes** by default — "report, don't act" — and `--stall` overrides it.
- Every new flag goes in ai-skills (the bobcat authoring skill's running section).

### 2026-10-16 — G6 + G12, stop conditions (Jeremy)

- **G6 stays dropped for 1.0 and after.** An exception ends the scenario; continuing would mean
  swallowing it. Assertions still gather every wrong, and `SpecCriticalException` is the explicit
  abort.
- **G12 (`BreakOnWrongs`) is not built until someone asks.** Gathering every wrong gives an agent
  strictly more information; the only use is a long slow spec where everything after the first wrong
  is noise.

### 2026-10-16 — G7, selection lists (Jeremy) — **never**

- **Dropped.** Use enums: a bad enum cell already fails naming the alternatives. The editor
  drop-down was most of what selection lists were for, and lists drawn from data would need the
  service locator core does not have. Added to the deliberately-dropped table.

### 2026-10-16 — G8, value-check sentences; and the framing (Jeremy)

- **G8 dropped.** Don't port automatic return-value comparison; `SpecAssert.Check` and projected
  Shouldly assertions already make the cell.
- **Framing for the whole record:** Storyteller is an influence, but it has been a dead project for
  years with no real usage. **No conversion documentation from Storyteller and no migration
  tooling.** Gaps are judged on their value to Bobcat users, never on parity. (Recorded at the top
  of this file.)

### 2026-10-16 — G9, logs and diagnostics (Jeremy) — **(a) 1.0**

- **(a) Step-level log lines, 1.0.** A static, `AsyncLocal`-backed `SpecLog.Write(...)` (name
  settled at implementation) records onto the step in progress; `RecordedStep` gains a log list
  (the render model's slot already exists). Rendered under the step in console, JSON, HTML and the
  wire; latched like reports (`OnFailure`, lifted by verbose); capped per step with "…and N more".
  The value is diagnosis — "what did the system do?" — above all for an agent reading a red spec.
- **(b) Capturing the app's `ILogger`: post-1.0, needs an attribution design first.** Parallel
  tests plus handlers logging on background threads (where the scenario's `AsyncLocal` does not
  flow) would pin logs to the wrong spec, which is worse than no logs. WolverineFx.Bobcat's
  tracked-session envelope report already covers messaging.
- **(c) Named timing records: dropped.** Steps are timed; a `SpecReport` can carry durations.

### 2026-10-16 — G10, setup failures (Jeremy) — **folded into G1, 1.0**

- **G1's halt must also fire when the catastrophic failure happens in setup**, not only inside a
  scenario: a class/assembly fixture, `IAsyncLifetime`, or host startup. That *is* the canonical
  Playwright case: the app is started from a fixture before any scenario opens, so a scenario-only
  halt would miss it. The first test reports the real startup error; every later test in the
  process fails immediately with "environment lost: <the startup failure>".
- Implementation note: per adapter this needs a probe. In xUnit v3 a failed class fixture fails
  each test in the class, but Bobcat's before/after bracket may never run. Prove each framework
  against a real host, the way `Probes.cs` does for NUnit/MSTest.
- **The rest of G10 is dropped:** setup is not rendered as spec steps and not timed (#141).

### 2026-10-16 — G11 + G15, integrations (Jeremy) — **never in Bobcat**

- **Relational database grammars, Selenium, Redux, TestRail: never in Bobcat.** Storyteller needed
  a database grammar because it could not run arbitrary C#; a Bobcat spec queries with EF Core,
  Dapper or Marten and verifies with `ObjectSetVerification`/`SetVerificationComparer`. TestRail
  would be a third-party plugin on `ScenarioCompleted` / the wire / `ISupervisorObserver`.
- **HTTP request report: already covered in WolverineFx.Bobcat.** Checked in `~/code/wolverine`:
  `WolverineHttpScenario` records verb, route, status, request body and response body into an
  `HttpExchangeReport` through `SpecReport`. Not a Bobcat core item.

### 2026-10-16 — G13 + G14, smaller items (Jeremy)

- **1.0:** `PropertyCells` reads fields as well as properties (partial objects already do); a cell
  literal that cannot convert is an `invalid` cell, not a row error (done with G3 — it is a spec
  defect); delete the dead `SpecRender.Timeline` / `JsonTimelinePointOutput` before 1.0 freezes
  them as public API; fix the documentation drift in §6.
- **Post-1.0:** a BOBCAT030 replacement (constant arguments checked at compile time);
  `bobcat vocabulary` (every step helper's text and signature, for agents and analysts); living
  docs (embedding rendered specs in documentation — falls out of G2's Vue/HTML work).
- **Never:** `PropertyCells` over no-arg methods; per-set column headers/defaults; an
  `ApiFixture<TIn,TOut>` helper (`WolverineHttpSpec` covers it); JSON-text and CSV grammars;
  profiles and `--prop`; CSV performance output. A spec with no steps rendering OK needs no change
  (`SuiteTiming` flags scenarios that assert nothing).

### 2026-10-16 — 1.0 scope from this pass

Every gap now has a decision. What this record puts on the 1.0 list:

1. **G1 + G10** — worker `ErrorType` → Catastrophic in the Supervisor; in-process halt after a
   catastrophic failure, including one thrown from setup/fixtures/host startup (probe each adapter);
   documentation with the Playwright-app-won't-start example.
2. **G2** — JSON results file; opt-in self-contained HTML results page built with Vue.
3. **G3** — `IParsable`/`TypeConverter`/`ctor(string)` fallbacks, `CellValues.Register<T>`, comma
   arrays/lists on step and row parameters, unconvertible literal → `invalid`.
4. **G4** — `Lifecycle.Acceptance` (runs, platform skip on failure, distinct verdict, never
   retried, "ready to promote", `BOBCAT_LIFECYCLE=strict`); wire change coordinated with Stoat.
5. **G5** — `bobcat run --retries/--workers/--stall/--stall-action/--results`; supervised runs
   report stalls at 5 minutes by default.
6. **G9a** — step-level log lines.
7. **G13/G14** — `PropertyCells` fields, delete dead Timeline, doc drift.

Each needs its ai-skills entry in the same piece of work (CLAUDE.md rule).

### 2026-10-16 — issues filed

Milestones `1.0` and `Post-1.0` created. Epic: #556.

- **1.0:** #538 (G1 bug), #539 (G1/G10 halt), #540 (G2 JSON), #541 (G2 HTML), #542 (G3 converters),
  #543 (G3 arrays), #544 (G13 invalid cell), #545 (G4 Acceptance), #546 (G5 CLI supervisor),
  #547 (G9a step logs), #548 (G13 fields), #549 (G14 Timeline), #550 (§6 doc drift).
- **Post-1.0:** #551 (G9b ILogger), #552 (`bobcat vocabulary`), #553 (living docs),
  #554 (BOBCAT030 replacement), #555 (G3 service-backed converters, wait and see).
