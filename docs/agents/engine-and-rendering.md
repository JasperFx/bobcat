# Engine, rendering, reports and failure semantics

### Engine (`src/Bobcat/Engine/`)
- **A cell says what it compared (`Engine/Comparison.cs`, issue #384 part B).** `CellResult` was
  equality-shaped by construction — `Expected` + `Actual`, rendered `expected 'x', got 'y'` — so a
  cell produced by a non-equality assertion **stated something false**: Shouldly interception shipped
  cells with no comparison, and `ShouldBeGreaterThan(10)` against 3 rendered `expected '10', got '3'`
  where 10 is the bound. The *sentence* was right, because the dialect writes the comparison into the
  step text, so the console read correctly and only a consumer rendering cells as a grid saw the
  false half. That is why it survived.
  - **The enum is closed, and being closed IS the whitelist** — `Equals` / `NotEquals` /
    `GreaterThan` / `GreaterThanOrEqual` / `LessThan` / `LessThanOrEqual` / `Contains` /
    `StartsWith` / `EndsWith` / `Approximately` / `IsNull` / `IsNotNull` / `IsEmpty` / `IsNotEmpty`.
    An assertion Bobcat has no member for produces **no cell at all** and the step renders as a
    plain line; a free string would let a producer invent a comparison nothing can render or check.
  - **It EXTENDS `CellResult` rather than replacing it**, which was #384's open question. Every
    existing producer is genuinely making an equality claim, so a **null default is honest** and
    reads as `Equals`; `DisplayText`'s legacy constructor is load-bearing for the set-verification
    path and must not be disturbed; and #396 settled that a cell says exactly one thing with its
    *content* fields, where a comparison is not content but a statement about how `Expected` and
    `Actual` relate — orthogonal, so it sits beside them. `WithNote` **carries it**, because without
    that, appending a note turned a `GreaterThan` cell back into an equality claim.
  - **Rendered in one place, `CellResult.derive()`**, which is what the Spectre grid, the inline
    sentence, the JSON report and the wire all already read — so they cannot disagree about what a
    comparison said. #384's acceptance asked for a two-column table only for an all-`Equals` group;
    there is no separate two-column shape in this renderer to switch on, so the faithful reading is
    that each cell states its own, which gives a mixed group per-row rendering by construction and
    an all-equality group byte-identical output.
  - **Acceptance 4 is the overload the generator picks** (`StepInterceptors.emitAssertion`): no
    comparison means the cell-less `Gather`, so the decision is compile-time and the runtime never
    judges whether a cell it was handed is describable.
  - **The interceptor's signature must match exactly, and two shapes did not (issue #410).** Both
    broke the **consumer's** build, in a generated file the author cannot edit, over assertions that
    compile perfectly without Bobcat.
    - **The return type was always written `void`.** `ShouldNotBeNull<T>` and `ShouldBeOfType<T>`
      return `T`, so a statement-level call to either was CS9144. Fixed by emitting the real return
      type, capturing the result inside the `Gather` lambda and handing it back. Returning `default!`
      after a *gathered* failure is safe for a reason that already existed: only a **statement-level**
      call is intercepted (`ProjectedAssertions.StatementOf`), so the value is discarded at the call
      site by definition — `x.ShouldNotBeNull().Name.ShouldBe("a")` consumes the result, is never
      intercepted, and throws as it always did.
    - **An enum default was emitted as its number.** `ExplicitDefaultValue` boxes an enum as its
      *underlying primitive*, so `defaultOf`'s `IsPrimitive` guard caught it before the fallback its
      own comment promised, and `Case caseSensitivity = Case.Insensitive` came out as `0` — CS1750.
      That took out `ShouldContain`, `ShouldStartWith` and `ShouldEndWith` over a string. Emitted as
      a **cast** rather than as `default`, which would be right only for a zero-valued member.
    - **`CanReproduce` is the guard that keeps the next one from reaching a consumer**, extending
      #384's degradation from "no comparison for this name" to "no signature that can match this
      call": by-ref parameters and by-ref returns cannot survive the `Gather` lambda, so no
      interceptor is emitted and the assertion runs as it always did. A lost cell is a small,
      visible loss; a build that will not compile is not.
    - **Why nothing caught any of it, and what now does.** `AssertionComparisonTests` pins the
      dialect's mapping — a pure function from a method *name*, testable without a compilation, which
      is the right design and stays. Nothing tested that a claimed name could be *intercepted*, and
      the halves are in different assemblies, so the only thing that failed was somebody's build.
      `ProjectedAssertionCompilationTests` now generates and **compiles** every claimed name through
      the real generator. Two things the harness needed for that, each a trap in its own right:
      projected assertions are **opt-in per project** (`build_property.BobcatProjectAssertions`), and
      C#'s `interceptors` feature must be enabled for `Bobcat.Generated` — without either, the
      generator emits nothing and a compile assertion passes over an empty file. **It did: the first
      cut of that theory went green on 15 rows having generated nothing at all**, which is why every
      row now asserts the interceptor exists before asserting it compiles.
  - **The Shouldly mapping is a pure function of method name plus has-a-tolerance**
    (`ShouldlyDialect.ComparisonFor`), testable without a compilation — and `ShouldBe(x, 0.01)` is
    `Approximately`, the one case the name alone cannot settle. **The halves are in different
    assemblies** (the netstandard2.0 generator cannot reference the runtime), so a typo would emit
    `Comparison.GreatherThan` and break the *consumer's* build in a generated file; a test checks
    every mapped name against `Enum.GetNames<Comparison>()`.
  - On the wire it is a trailing optional camelCase word on `StepCell`, **null meaning equality** —
    so a consumer that ignores it is exactly as correct as it was.
- **A scenario can report an account of itself, not only a verdict (`Engine/ScenarioReport.cs`,
  issue #408).** `IScenarioReport` is a titled grid of cells describing what the system *did* —
  Storyteller's custom logging (`ISpecContext.Reporting`, `Report.ToHtml()`) answered with the Cell
  model instead of HTML. Built for asynchronous messaging specs, where the evidence that diagnoses
  a failure is a tracked session's envelope record and belongs to no single step.
  - **Scenario-scoped is the whole point.** `SpecReport.For<T>()` accumulates onto
    `ScenarioRecorder.Recording.Reports`, one instance per report type per scenario — which is how
    several grammars append to one table agreeing on nothing but the type, Storyteller's
    `ReporterFor<T>()`. It sits beside `TouchedTypes` on the recording, because a report must reach
    the wire for the same reason run evidence does.
  - **`SpecReport` is static, and that is load-bearing rather than convenient.** A producer in
    another package (`WolverineFx.Bobcat`) must compile **once** and work inside any projected test,
    which has no context object to hand it. So the surface is `AsyncLocal`-backed and ambient
    (`ScenarioReports`), the same answer `ScenarioRecorder.Current`, `BobcatClock` and `SpecAssert`
    already give to the same problem. `BothLanesReportTests` pins the shared producer.
  - **Rows are unjudged `Value` cells (#396), so a log table cannot claim it checked anything.** A
    report that *does* want to judge one row — the envelope that was dead-lettered among a dozen
    that were fine — builds an ordinary judged `CellResult` for it, so a red row inside an
    informational table needs no second mechanism. The grid folds through
    `SetVerificationRender.FromCells` and renders through `CommandLineRenderer.RenderSetVerification`,
    so a report's table and a set verification's table cannot look different, and
    `ShowsStatusColumn` already drops the Status column for an all-unjudged grid.
  - **`ReportVisibility` is `OnFailure` by default and `run --json --verbose` is the only thing that
    lifts it** (decided 2026-10-06). `ScenarioReportVisibility` is the one rule all three surfaces
    ask — console, JSON report and wire — so they cannot disagree about what a run reported, the
    same discipline `CellResult.derive()` follows. The rejected alternative was JSON always carrying
    everything on the argument that an agent reads the archive afterwards and volume there is cheap:
    it makes JSON an exception to the uniform rule, and an agent that wants everything can ask and
    then gets it everywhere at once. `BOBCAT_VERBOSE_REPORTS` is the environment's spelling, read as
    a tri-state through `ProjectedSpecConsole.Setting` so "nobody said" stays distinguishable from
    "somebody said no". An **empty** report is dropped whatever its visibility — a grammar that was
    never exercised legitimately produces one, and an empty grid under a heading says less than no
    heading.
  - **200 rows per report, capped, with "…and N more" — never a silent truncation.**
    `docs/tutorials/agent-friendly-tests.md` records what happens without a cap: a suite dumped a
    698MB tracked-session log into test output, faulted the worker, and turned every unreported test
    indeterminate — a run that read as a crash because something tried to say too much. The cap is
    in `TableReport` rather than left to each producer to remember.
  - **On the wire: `ScenarioFinished.Reports`, trailing and optional**, carrying
    `ScenarioReportInfo` whose cells are `StepCell` — one cell shape on this wire, not a sibling
    record. `MonitorReports.From` is the single projection and applies the visibility rule itself,
    the `StepCells.From` lesson from
    #396: two copies of one projection is what once silently dropped a literal's text.
    Null rather than an empty list when there is nothing, so "reported nothing" and "a publisher too
    old to know about reports" look the same — because they mean the same thing.
  - **One core fix fell out of building it.** `CellResult.WithNote` copied through the *structured*
    constructor and so dropped a legacy-constructed cell's private `_displayText`, leaving
    `DisplayText` to derive the empty string for a plain value cell. That is #396's defect in the
    model rather than in a projection, and it went unnoticed because every existing caller happened
    to pass structured cells. `WithNote` and the new `WithRowIndex` now share one private `copy`,
    and any future "a copy of this cell, but…" belongs there.
- **A projected spec's logs and reports reach the runner's own test output (`SpecOutput`, issue
  #409).** The one place a developer looks when `dotnet test` goes red is the platform's output
  block for that test, and nothing Bobcat knew was reaching it: `ProjectedSpecConsole` deliberately
  stays silent under a captured stream, because a captured stream belongs to whoever captured it.
  - **Core names neither runner's types.** The seam is an `Action<string>` of lines, supplied by the
    adapter — the third thing on the marker-step adapter seam after the two verdict callbacks.
    `Bobcat.Xunit` hands over `TestContext.Current.TestOutputHelper.WriteLine`, which is **ambient
    in xUnit v3** and so costs a `[BobcatSpec]` test no constructor parameter — requiring one would
    spend #403's whole point. `Bobcat.TUnit` hands over `context.OutputWriter.WriteLine`, because
    TUnit has no `ITestOutputHelper` at all; that asymmetry is why the seam is a delegate rather
    than an interface borrowed from one of them.
  - **`AsyncLocal`, because tests run in parallel.** A static sink would send one test's lines to
    another test's output, which is worse than sending them nowhere.
  - **Logs go through as they happen; reports go at the close.** A log line is written immediately,
    so a test that also writes to its own output reads as one stream in source order rather than as
    two blocks. A report is an account of the whole scenario and cannot exist until it closes, so it
    is written from `ScenarioRecorder.ScenarioCompleted` — after the verdict is known, which is what
    the visibility rule needs anyway. The adapters therefore dispose the sink **after**
    `EndScenario`, not before.
  - **`WriteReports` takes the `SpecRender`, not the reports.** `SpecRender.Reports` has already
    applied `ScenarioReportVisibility` and already knows whether the scenario failed, so the output
    block cannot reach a different answer than the console and the JSON report did.
  - **Logs always, reports latched — and that asymmetry is deliberate.** Bobcat does not latch the
    log line, because the platform already attaches per-test output to the result and shows it for
    failures; a second opinion about the same question is how the two disagree, and withholding on a
    pass would leave a *passing* test somebody is debugging with no output. A report is up to 200
    rows a grammar produced whether or not anyone wanted them, so it honours `ReportVisibility`.
    Where Bobcat owns the whole surface — the JSON report — it latches uniformly.
  - **Writing can never fail a test.** Every call is guarded: a runner whose output helper has been
    torn down throws, and a specification that reported something must not go red because the
    reporting channel closed first. The monitor publisher's invariant, applied here.
  - **`TextGrid` is a second renderer, not a reuse of the Spectre one.** `ITestOutputHelper` and
    TUnit's writer are plain text captured by the platform, so markup would arrive as escape codes
    in exactly the place a failure is read. The row that disagreed is marked `<-- FAILED` in text
    rather than coloured, so it is findable by eye in a CI log and by grep — neither of which sees
    an ANSI code. Same model, two outputs, which is the split `SpecRender` exists for.
  - **`PlainSpecText` (`Bobcat.Rendering`) is where the plain-text rendering lives**, and
    `SpecOutput` delegates to it, so there is one copy of the rendering — the lesson `StepCells.From`
    already taught (#396).
- **Step logs (`SpecLog.Write`, bobcat#547, G9(a))** record onto the open step's `RecordedStep.Log`, or
  `Recording.Log` between steps (a `StepLog`, capped at `MaxLines` = 100 with a suppressed count, "…and N
  more"). Latched by `ScenarioReportVisibility.ShouldWriteLogs` — the report rule, applied once in
  `SpecRender.FromRecording` (console, JSON, plain text, runner output) and once in `MonitorLogs.From`.
  **On the wire they ride `ScenarioFinished.Logs` (`StepLogInfo`, keyed by step id, null id = between
  steps), not `StepFinished.Logs`**, because the verdict that latches them is only known at the close;
  `bobcat ui` folds them onto its steps. App `ILogger` capture is G9(b), post-1.0.
- **Failure evidence: `SpecAttachment` and `IFailureCapture` (bobcat#571, `MarkerSteps/SpecAttachment.cs`).**
  `SpecAttachment.Add/AddText/AddFile` hangs bytes on the step in progress (`RecordedStep.Attachments`) or
  the scenario; `RegisterFailureCapture` is called from `EndStep` for the **innermost** failed/error step
  only, on `Task.Run` (no sync context to deadlock; the test's AsyncLocals flow) bounded by
  `CaptureTimeout`, and a throw or overrun is logged to `SpecOutput`/stderr and stepped over. No general
  step hooks, by decision (`design/bdd-tools-comparison.md` C2). **Latched in `Recording.Dispose` before
  anything reads the recording** (`ScenarioReportVisibility.Verbose` or a failure keeps them, else they are
  cleared), then `AttachmentFiles.Write` puts them in ONE place: `attachments/{runId}/{uid}/{nn}-{name}`
  under `ResultsFile.Active` when there is a results directory, else the adapter's platform sink
  (`OpenPlatformSink` bytes for xUnit's `AddAttachment`; `OpenPlatformFileSink` temp files for TUnit
  `AttachArtifact`, NUnit `AddTestAttachment`, MSTest `AddResultFile`), numbered because xUnit refuses a
  duplicate name. Then the bytes are released. Over `MaxBytes` is a reference marked `Omitted`, never a
  truncation; past `MaxPerScenario` is counted (`SuppressedAttachments`, "…and N more"). The wire
  (`ScenarioFinished.Attachments`, `ScenarioAttachmentInfo`) and the results file carry references only.
  The HTML page (#541) is to render them inline under their step from the results file's `path`.
- **`StepKind`** / **`FailureLevel`** — drive failure classification.
- **In-flight progress:** `ScenarioRecorder` posts `step_progress`, and `TotalSteps` / `StepNumber` /
  `ScenarioElapsedMs` ride the step events; a long table's progress is its cells-so-far update
  (#387). Details in `docs/monitor-design.md`.
- **Run evidence (#107, #465):** `ScenarioRecorder.RecordTouchedType(Type)` accumulates onto the
  recording and reaches `scenario_finished.TouchedTypes` through `MonitorTouchedTypes` (JasperFx
  `TypeDescriptor`'s three fields, `FullName` joining a design-time
  `SpecificationDescriptor.ResolvedTypes`). Observed, never asserted. Nothing recorded is null, not
  an empty list.
- **Timeline analysis (issue #142, items 1–3 only):** `SuiteTiming.For(recordings)`
  (`Runtime/SuiteTiming.Projected.cs`) is the in-process sibling of the supervisor's `RunTiming` —
  pure computation, rendered as `CommandLineRenderer.RenderTimingSummary` and the `timing` block of
  `JsonRenderer.RenderTiming`. Report, don't act — same guardrail as `RunTiming` and #44's hints.
  Gap ranking, per-step aggregation grouped by *normalized* step text (`NormalizeStepText` folds
  quoted strings/bare numbers to placeholders), and scenarios that assert nothing. **Lifecycle is
  not measured** (`LifecycleMeasured` is false): it belongs to the test runner — `IAsyncLifetime`,
  class and assembly fixtures, TUnit's hooks — and Bobcat neither owns nor sees it; a consumer
  renders "not measured", never a zero (#141's rule). Item 5 (cross-run trends) ships on the
  committed ledger (`TestLedger.Trends()`, `design/ledger-design.md`); item 4 (sleep-shaped
  durations) stays unbuilt — its false-positive question is open, and `[SlowByDesign]` is a
  JasperFx.Testing decision.

### Rendering (`src/Bobcat/Rendering/`)
- **`SpecRender`** — the intermediate model every output reads (`SpecRender.FromRecording`)
- **`CommandLineRenderer`** — Spectre.Console ANSI output; set verifications render as a table with
  per-cell colouring
- **`TextGrid`** / **`PlainSpecText`** — plain text for a platform's captured test output
- **`JsonRenderer`** — the JSON report
- **`ResultsFile`** — the JSON results file (bobcat#540): one file per run bracket, every scenario's
  `SpecRender` through `JsonRenderer`, opted into with **`BOBCAT_RESULTS=<dir>`** (so plain
  `dotnet test` works) and set by `bobcat run --results <dir>` / `--json` (the file on stdout, all
  else on stderr) / `--verbose` (`BOBCAT_VERBOSE_REPORTS`). `MarkerStepRun.openRun` calls
  `BeginRun`, `CloseRun` calls `EndRun`, so a warm process writes one per request. Named
  `<suite>-<runId>.json`, `-2`… on a clash (`FileMode.CreateNew`, supervised workers share a run
  id); never fails a run. **The schema is generated and never committed** (#604):
  `ResultsFile.GenerateSchema()` writes it beside every file, the Nuke `Pack` target packs it under
  `schemas/`, and `CI`'s `ResultsSchemaCompatibility` fails a breaking change against the last
  released package's copy unless `SchemaVersion` moved. An additive field does not. #541's TypeScript types are to be generated from this schema.
- **`obj/bobcat/last-run.json`** (`Bobcat.Console/Specs/LastRun.cs`, bobcat#572): every `bobcat run`
  writes it in the spec project: failed identities, selection, flags, exit code, a reserved `seed`.
  `run --failed` / `--repeat` read it. The failures come **from the results file**, so `SpecHost`
  now always sets `BOBCAT_RESULTS`: a private per-run temp directory (deleted afterwards) when
  `--results` isn't given. An identity the manifest lost is refused through `SpecSelection.NotIn`;
  a host that wrote no results leaves `resultsRead: false`, and `--failed` refuses rather than reading
  that as "nothing failed". `RunFailedAndRepeatTests` (Supervisor.Tests) drives the real tool.

### Three-Level Failure Semantics
1. **Assertion** — a gathered wrong (`SpecAssert`, a `[Check]` returning false,
   `SpecAssertionException`) → continue, gathering every failure
2. **Critical** — `SpecCriticalException` or any other exception in a step → abort the scenario
3. **Catastrophic** — `SpecCatastrophicException`: unrecoverable infrastructure (host won't start,
   database gone). Never retried — `DefaultFailurePolicy` and `HintedFailurePolicy` both refuse it.
   **Out of process (bobcat#538)** the supervisor only has the worker's reported type *name*, so
   `Supervisor.failureLevelFor` maps an outcome whose `ErrorType` is exactly
   `Bobcat.Engine.SpecCatastrophicException` or `…SpecEnvironmentLostException` (#539; or the bare
   simple names) to `Catastrophic`, whatever
   the state (xUnit reports a throw as `failed`), and the run aborts. TUnit erases the type, so
   there it degrades to an ordinary Critical. **Critical stays retryable under a `Retry` trait**
   (decided 2026-10-16, gap G1): the flakiness that trait exists for mostly arrives as exceptions;
   `[NeverRecovers]` is the per-type opt-out. Pinned against the real worker by
   `SupervisorEndToEndTests.a_workers_catastrophic_failure_aborts_the_run_and_is_never_retried`.

**A catastrophic failure halts the rest of the process (bobcat#539, G1 + G10).** `SpecEnvironment`
(`MarkerSteps/SpecEnvironment.cs`) latches the first `SpecCatastrophicException` thrown anywhere in
the process, and `MarkerStepRun.begin` turns every scenario opening afterwards into a published
failure plus a thrown `SpecEnvironmentLostException` ("Environment lost in Feature/Scenario: …", or
"Environment lost: …" from setup), which every adapter's opening hook surfaces as the test failing
unrun. Decisions worth not re-deriving:
- **The latch is `AppDomain.FirstChanceException`, not a verdict an adapter reads**, because the
  canonical case (a Playwright app that won't start) fails in setup — a class fixture,
  `IAsyncLifetime`, `[OneTimeSetUp]`, `[ClassInitialize]`, `[Before(Class)]` — where the bracket
  never runs (xUnit v3 fails a class fixture's tests without invoking `BeforeAfterTestAttribute`).
  The throw is the one place every route passes through. Consequence: a thrown-and-caught one counts.
- **Armed before any setup can run**: each adapter's attribute has a static constructor (the runner
  instantiates attributes during discovery), `BobcatSessionLifetime` arms at session start, the
  MSTest hook source carries a `[ModuleInitializer]`, and `begin` arms again. No library module
  initializer in core: a process that never touches an adapter (the supervisor, `bobcat ui`) is
  never armed.
- **The halted scenario is closed inside `begin`**, before the throw, because not every runner calls
  its after-hook once the before-hook threw. TUnit marks the tests it counted (`StateBag`), so a
  halted test cannot skew the deferred session close.
- `SpecEnvironmentLostException` derives from `SpecCatastrophicException` (never retried) and carries
  **no inner exception**, so hundreds of halted tests don't print the startup stack hundreds of times.
- Proven per adapter against a real host, one case per child process (the halt is process-wide):
  `Bobcat.Xunit.Tests`/`Bobcat.NUnit.Tests`/`Bobcat.MSTest.Tests` `EnvironmentLossTests`, and
  `Bobcat.Supervisor.Tests/TUnitHaltTests` over `Bobcat.TUnit.Tests/Halt`. Probe ordering: xUnit
  needs a collection-level `[TestClassOrderer]` (a case orderer did not take effect), MSTest
  `[DoNotParallelize]`, TUnit `[DependsOn(…, ProceedOnFailure = true)]`. User docs:
  `docs/marker-steps.md#environment-lost`.
