# Testing strategies from Gauge, Reqnroll and Cucumber

**Design record, started 2026-10-16.** A comparison of Bobcat's current and planned features (the
1.0 scope in `storyteller-gap-analysis.md`, epics #524 and #443) against Gauge, Reqnroll and
Cucumber — looking for **testing strategies** worth having, not syntax.

**Ground rule (Jeremy, 2026-10-16): Bobcat takes on no Gherkin or Markdown feature set.** Each
tool's authoring layer is ignored; the question is always "what strategy sits under this feature,
and does a code-first C# framework benefit from it?" Exporting *to* a format others read is not
authoring and is allowed to be considered (C10).

Same conventions as the Storyteller record: candidates have ids (C1…), each has a **Decision**
cell, and the [Discussion log](#discussion-log) at the bottom is append-only.

---

## 1. Verdict

Most of what these tools ship exists because **prose has to be bound to code at run time**: step
matching, undefined/ambiguous/pending steps, scoped bindings, step aliases, dry runs, snippet
generation, data stores, context/DI containers, argument transformations from step text. Code-first
removes that whole category — the compiler does the binding, and those failure classes cannot
occur. Bobcat is also already **ahead** on comparison (per-cell verdicts, Kuhn-matched sets,
partial objects, compile-time column checks), resilience (failure-class retries, `PassOnRetry`,
ledger, isolation, stall kill — every one of these tools retries blindly), parallelism
(class-partitioned, duration-balanced lanes) and traceability (spec identity joined to the Event
Model slice — none of them has anything like it).

What genuinely transfers falls in four groups:

1. **Data-driven identity** — a theory row is its own specification (C1).
2. **Failure evidence** — attachments and a step-completed seam for screenshots and dumps (C2).
3. **Run ergonomics** — rerun failed, sharding, lazy distribution, seeded random order, selection
   by Event Model coordinates (C3, C5–C8).
4. **Rules and questions** — Example Mapping's middle layer on the slice (C4).

---

## 2. Candidate register

| Id | Strategy | Seen in | Bobcat today | Proposed shape | Decision |
|---|---|---|---|---|---|
| **C1** | **Each data-driven row is its own specification** | Scenario Outline/Examples (Reqnroll, Cucumber), spec data tables (Gauge) | `[Theory]`/`TestCase` run, but the bracket opens from the `MethodInfo` only (`XunitScenarioBracket` → `MarkerStepRun.BeginScenario(method, …)`; TUnit the same), so **every row shares one `{Feature}/{Scenario}` identity**: run-by-identity can't pick a row, the ledger mixes rows' durations and flakiness, Stoat gets N `scenario_finished` under one uid. `[BobcatSpec]` is a `[Fact]`, so there is no one-attribute theory form. `TableRunner` covers *many rows in one spec* but not a verdict per example. | `[BobcatTheory]` (the #403 trick: `TheoryAttribute` + `IBeforeAfterTestAttribute`), identity `…/Scenario [label]` or `…/Scenario (amount: 100, tier: Gold)` from the row's arguments (`TestContext.Current.TestMethodArguments`; TUnit `TestMethodArguments`); constant rows listed in the manifest; BOBCAT036 for duplicate labels; non-constant `MemberData` falls back to the method identity. Optionally render the rows as one Examples grid. | **Decided 2026-10-16 — 1.0, see log.** |
| **C2** | **Failure evidence: attachments plus a step-completed seam** | Screenshots on failure + custom grabbers (Gauge), `AfterStep` + `TestError` + `AddAttachment` (Reqnroll), `attach()` + `BeforeStep/AfterStep` (Cucumber) | No attachments anywhere in `src/`. `ScenarioCompleted` is the only public event. `SpecLog` (#547) is text only. | `SpecAttachment.Add(name, bytes, mediaType)` onto the step in progress; a registered `IFailureCapture` (or public `StepCompleted` event) so a Playwright base class screenshots every failed step with no per-spec code. Latched like reports, size-capped; files beside the JSON/HTML results (#540/#541), a reference on the wire (never inline bytes — the 698 MB lesson); a capture that throws never fails the test. Serves G1's Playwright story directly. | **Decided 2026-10-16 — 1.0, see log.** |
| **C3** | **Rerun what failed / repeat the last run** | `--failed`, `--repeat` (Gauge), rerun formatter (Cucumber) | Nothing. Pieces exist: spec identities, `SpecSelection`, `SpecFilterArguments`, and #540's results file. | `bobcat run --failed` (last results' failed identities), `--repeat` (last invocation's arguments, seed included); identities the manifest no longer has refused by name (`SpecSelection.NotIn`), never an empty run exiting 0. Highest-frequency need in the agent red-green loop. | **Decided 2026-10-16 — 1.0, see log.** |
| **C4** | **Example Mapping: rules and questions on the slice** | `Rule` keyword and Example Mapping practice (Cucumber); `Rule:` grouping (Reqnroll) | Slice ≈ story, board examples → specs (`import --specs`), hotspots exist (`HotspotOrigin.Prose`, `PendingSpecification`). **No rule layer, no question construct.** | `[BobcatRule("Cannot withdraw more than the balance")]` (nested class or on the spec) stamped onto `SpecificationDescriptor`; `[BobcatQuestion("…")]` becomes a hotspot (red card); `EventModelSliceBuilder.Rule(…)` so definition-first (#443) carries rules forward. Coverage per rule: no examples = hotspot, all green = done, many rules = split the slice. **Needs a JasperFx descriptor change.** | **Decided 2026-10-16 — post-1.0, designed now; see log.** |
| **C5** | **Select by Event Model coordinates (and traits) from the CLI** | Tag expressions (Cucumber, Gauge) | Framework trait filters only; `bobcat run` has `--feature`/`--scenario` substring filters. | `bobcat run --slice X --chapter Y --pattern View --rule Z`, or `--where "chapter:Banking and not pattern:Automation"`; plus `--trait`. Resolved through the `SpecManifest` (add slice/chapter/rule to entries) into identities, so "refuse rather than guess" holds. Pairs with `bobcat ui`: click a slice, run its specs. | **Decided 2026-10-16 — 1.0, see log.** |
| **C6** | **Deterministic sharding across machines** | `-n -g` groups (Gauge), `shard: i/n` (Cucumber) | `WorkPlan` partitions and balances lanes within one machine; nothing across machines. | `bobcat run --shard 2/4`: the same class-partitioned, ledger-balanced plan one level up; deterministic for a manifest + ledger so legs never overlap or drop a class. | **Post-1.0 — 2026-10-16, see log.** |
| **C7** | **Lazy (work-pulling) lane distribution** | Gauge's default `lazy` | `WorkPlan` is eager LPT only. | `Supervisor.Distribution = Lazy`: lanes pull the next class partition from a longest-first queue; same-process retries still return to their lane. Self-corrects when durations are unknown (first run, no ledger). | **Post-1.0 — 2026-10-16, see log.** |
| **C8** | **Seeded random order** | `--sort random --random-seed` (Gauge), `--order random:seed` (Cucumber) | None. | `--order random --seed N`: shuffles class partitions and lane order only, never tests within a class; seed printed and stored in results so `--repeat` reproduces it. Finds cross-class coupling (the `static int count` case). | **Post-1.0 — 2026-10-16, see log.** |
| **C9** | **Vocabulary usage: unused and slow steps** | Usage formatter (Cucumber) | `SuiteTiming` ranks steps by normalised text (slow half). Nothing for unused. `bobcat vocabulary` is post-1.0 (#552). | Extend #552: per helper — calls, specs, p95 duration; an info diagnostic for a step helper no specification calls. The generator sees every call site, so this needs no run. Stops agent-written vocabularies sprawling. | **Post-1.0 with #552 — 2026-10-16, see log.** |
| **C10** | **Interop results export** | Cucumber Messages `.ndjson` (Cucumber, Reqnroll) read by Allure, ReportPortal, Xray | Bobcat's own wire and the planned JSON; no standard format. | `--format cucumber-messages` (or Allure): a one-way projection from `SpecRender` — a spec becomes a Pickle synthesised from rendered steps, cells become DataTables, C2 attachments become Attachment messages. **Export only: nothing reads Gherkin.** | **Post-1.0, Allure first, no Cucumber Messages — 2026-10-16, see log.** |
| **C11** | **File-backed examples** | `<table:x.csv>` (Gauge), External Data plugin (Reqnroll) | `StepTable.Parse(string)` only; framework `MemberData` can read files. | Only after C1: `[BobcatExamplesFrom("discounts.csv")]` read by the generator through MSBuild `AdditionalFiles`, so the data stays a compile-time constant — BOBCAT032's column check and per-row identities survive. | **Decided 2026-10-16 — 1.0 as inline pipe-table examples, not files; see log.** |
| **C12** | **One behaviour per specification** | Cucumber/BDD practice (one `When`) | The act is the last command call (#464); arranging commands allowed. | Info diagnostic when a spec acts again after its first assertion; and say it in the authoring skill. | **Dropped 2026-10-16, see log.** |
| **C13** | **Strict mode also fails Pending** | Cucumber `strict` (undefined/pending fail the run) | `BOBCAT_LIFECYCLE=strict` (#545) fails Acceptance specs only. | Should strict also turn `Pending = true` into a failure, for a branch claiming done? | **Decided 2026-10-16 — 1.0 with #545, see log.** |
| **C14** | **A published schema for the wire** | Cucumber's protobuf-defined messages | `MonitorEvents.cs` here, a hand-kept copy in Stoat; contract tests only. | Publish a JSON Schema for the monitor wire and the results JSON, so third parties (and #524's SPA) are checked against it. | **Decided 2026-10-16 — results schema 1.0, wire post-1.0; see log.** |
| **C15** | **Snapshot verification as a cell** | Reqnroll.Verify | None; JSON-text grammars dropped (G13). | `SpecAssert.Verified(x)` recording one cell for a Verify snapshot diff. A different strategy from partial/structural expectations. | **Post-1.0 as a separate package — 2026-10-16, see log.** |
| **C16** | **Practice guidance in the ai-skills** | Driver pattern (Reqnroll), declarative over imperative steps, testing-pyramid placement of acceptance tests (Cucumber) | The vocabulary is declarative and layered (store → message → HTTP) by construction, but nothing says so to an agent. | Write it into `critterstack-sdd-bobcat-authoring`: name the driver pattern for Playwright specs, keep steps declarative, prefer the lowest layer that proves the behaviour. No code. | **Decided 2026-10-16 — 1.0, see log.** |

---

## 3. Not transferable (code-first removes the need)

| Feature | Tools | Why Bobcat doesn't need it |
|---|---|---|
| Step matching, undefined / ambiguous steps, snippets for undefined steps, dry run for binding | all three | A step is a method call: a missing one is a compile error, an ambiguous one impossible. `bobcat preview` renders without running. |
| Scoped bindings `[Scope]`, step aliases, calling steps by text | Reqnroll, Gauge | One call resolves to one method; aliasing is a second method delegating to the first; helpers call helpers and nest in the rendering. |
| Data stores, ScenarioContext/FeatureContext bags, World object, DI container plugins | all three | Locals, fields and the test-class instance; specs resolve from the application's own host. |
| Step argument transformations from step text | Reqnroll, Cucumber | Step parameters are typed C# arguments. Cells are covered by #542/#543. |
| Contexts / Background / teardown steps rendered per spec | Gauge, Reqnroll, Cucumber | A shared arrange helper on the first line renders; setup belongs to the framework (G10). |
| `ContinueOnFailure`, `stopAtFirstError` | Gauge, Reqnroll | Assertions gather; an exception ends the scenario (G6, dropped). |
| Blind retries, run-wide fail-safe exit 0, generic fail-fast | all three | Bobcat retries by failure class with honest reporting; Acceptance (#545) is the per-spec fail-safe; catastrophic halt (#538/#539) is the better fail-fast. |
| Environments (`env/*.properties`), profiles | Gauge | .NET configuration (G14). |
| Hosted report publishing | Cucumber | `bobcat ui` (#524) plus the archived HTML (#541). |

---

## Discussion log

Append-only. One entry per discussion: date, candidate ids, who decided, what, and why.

### 2026-10-16 — comparison written

- Three parallel research passes (Gauge, Reqnroll, Cucumber), each mapped to Bobcat's current code
  and the 1.0 scope. C1's premise verified directly: both adapters call
  `MarkerStepRun.BeginScenario` with the method only, and no row arguments reach the recording.
  "No attachments anywhere in `src/`" verified by grep.
- Ground rule recorded: no Gherkin or Markdown feature set.
- To check: `StepArgumentSource.DocString` in `src/Bobcat/Runtime/StepBinding.cs` may be dead since
  the Gherkin lane went (like `SpecRender.Timeline`, #549).

### 2026-10-16 — C1, per-row identity for data-driven specs (Jeremy) — **1.0**

- **1.0, because identity is the contract** the ledger, Stoat, the spec manifest and the Event
  Model join on. Adding row identities after 1.0 would change existing identities and break every
  ledger entry and slice link a theory has.
- **Row identity = method identity + the row's named arguments**:
  `Discounts/Discount is applied (amount: 100, tier: Gold)`, values formatted invariant-culture so
  the string is stable across runs. `[BobcatExample("gold customer", …)]` gives a label instead:
  `…/Discount is applied [gold customer]`. BOBCAT036 covers duplicate rows/labels.
- **`[BobcatTheory]`**, the one-attribute form (`TheoryAttribute` + `IBeforeAfterTestAttribute`, the
  #403 trick); TUnit/NUnit/MSTest read row arguments from their own test context
  (`TestContext.Current.TestMethodArguments` in xUnit v3).
- **The Event Model keeps one specification per method** — rows are examples of one behaviour.
  Each row's result names its parent through a new trailing optional **`ParentSpecId`** on the
  wire, so Stoat folds rows into the method's verdict without parsing identity strings (docs/agents/runners-and-identity.md:
  never parse an identity). Wire change: coordinate with Stoat.
- Rows the generator cannot see (non-constant `MemberData`) still get row identities at run time
  but cannot be listed ahead in the manifest; documented.

### 2026-10-16 — C2, failure evidence (Jeremy) — **1.0**

- **Attachments API, 1.0:** `SpecAttachment.Add(name, bytes, mediaType)` / `AddFile(path)`, onto the
  step in progress (or the scenario). With a results directory (#540/#541) attachments are files
  beside the results, shown inline under the step in the HTML; under plain `dotnet test` they go to
  the platform's own attachments (xUnit v3 `TestContext.Current.AddAttachment`, MTP file
  artifacts) so TRX and the IDE show them; on the wire only a reference (name, media type, size,
  path), never bytes. Latched like reports (on failure; verbose keeps all), capped per attachment
  and per scenario with "…and N more".
- **Automatic capture, 1.0, through a narrow `IFailureCapture`:** registered once, explicitly;
  called when a step goes failed/error; returns attachments. A Playwright base class screenshots
  every failed step with no per-spec code. A capture that throws or runs past its time limit is
  logged and stepped over — it can never fail a test.
- **No general `BeforeStep`/`AfterStep` hooks.** A general hook invites behaviour hidden around
  steps (what makes Cucumber suites hard to read); the capture seam can only produce evidence.
  Revisit only on a real need.

### 2026-10-16 — C3, rerun failed / repeat (Jeremy) — **1.0**

- `bobcat run --failed`: the specs that failed or errored last time, by identity (C1 row
  identities included). `bobcat run --repeat`: the last invocation's selection and flags (and seed,
  if C8 lands).
- **`bobcat run` always writes a tiny state file, `obj/bobcat/last-run.json`** in the spec project
  (failed identities, arguments, seed, timestamp), so `--failed` works without `--results`. `obj/`
  is already git-ignored and cleaned by `dotnet clean`.
- An identity the manifest no longer has is **refused by name** (`SpecSelection.NotIn`), never an
  empty run exiting 0; nothing failed last time → says so, exits 0.

### 2026-10-16 — C4, Example Mapping: rules and questions (Jeremy) — **post-1.0, designed now**

- **Rules are declared on the model and claimed by specs.** Definition:
  `model.Command<WithdrawFunds>().Rule("Cannot withdraw more than the balance")` (and a board import
  carries rules where the board has them). A spec claims one:
  `[BobcatSpec(typeof(WithdrawFunds), Rule = "…")]`, with a class-level default allowed. Model rules
  and spec claims merge by text; a claimed rule the model does not declare is a hotspot, which also
  catches a typo.
- **Questions live on the model only**: `model.Command<X>().Question("…")` → a hotspot on the
  canvas. A red card is a conversation, not a test.
- **No nested class per rule**: a class name feeds the feature title, so moving a spec under a rule
  would change its identity.
- Read per rule: no examples = hotspot; all green = done; any red = in progress; many rules on one
  slice = split candidate (the Example Mapping heuristic, computed).
- **Post-1.0** because it is purely additive (no identity change), unlike C1. Needs: JasperFx
  descriptor (rules, questions, spec→rule), the generator reading `Rule =`, the canvas
  (Stoat / `bobcat ui` #524), and #443's definition-first pipeline carrying rules forward.

### 2026-10-16 — C5, selection by Event Model coordinates (Jeremy) — **1.0**

- `bobcat run --slice X --chapter Y --domain Z --pattern View` (`--rule` once C4 lands), plus
  `--trait Key=Value`. A repeated flag ORs, different flags AND.
- Resolved through the spec manifest (entries gain slice, chapter, domain, pattern — additive) into
  identities, so "refuse rather than guess" holds: a slice no spec is bound to is refused by name.
- **No expression language** (`--where …`) until someone needs NOT.
- Why 1.0: the spec-driven loop (and the code-from-specs skill) works slice by slice, and it is
  what `bobcat ui` (#524) needs to run a slice's specs from the canvas.

### 2026-10-16 — C5 addendum: the coordinates as MTP traits (Jeremy) — **1.0**

- **Two paths, one source.** `bobcat run` needs no MTP support: it reads the spec manifest,
  resolves `--slice` etc. to identities, and `SpecFilterArguments` hands the framework an ordinary
  method filter. **Also, for 1.0, the same coordinates are projected as platform traits** —
  `Slice`, `Chapter`, `Domain`, `Pattern` (and `Rule` with C4) — so plain
  `dotnet test --filter-trait`, IDE grouping, TRX and the Supervisor's discovery see them.
- **The generated binding is the single source of truth**; the manifest and the traits both read
  it, so they cannot disagree. Traits therefore carry the *derived* pattern too, not only what is
  written on the attribute.
- Per framework: xUnit v3 — `BobcatSpecAttribute` also implements `ITraitAttribute`, `GetTraits()`
  reading the binding the module initializer registered (needs a probe, like
  `BobcatSpecAttributeTests` pins the bracket trick); TUnit — a discovery-time receiver adding
  properties (probe); NUnit — `IApplyToTest`; **MSTest best-effort** (`Bobcat.MSTest` references no
  MSTest assembly, so only through its shipped hook source, if at all).

### 2026-10-16 — C6, sharding (Jeremy) — **post-1.0**

- `bobcat run --shard i/n` over `WorkPlan` (class-partitioned, ledger-balanced, deterministic for a
  manifest + ledger; count-balanced without a ledger). Post-1.0: purely additive.

### 2026-10-16 — C7, lazy distribution (Jeremy) — **post-1.0**

- `Supervisor.Distribution = Lazy` / `--distribution lazy`: lanes pull class partitions from a
  longest-first queue; classes never split; same-process retries return to their lane. **Eager stays
  the default.** Trade-off recorded: lazy makes which classes share a process timing-dependent —
  surfaces cross-class coupling, but less reproducibly.

### 2026-10-16 — C8, seeded random order (Jeremy) — **post-1.0**

- `bobcat run --order random [--seed N]`: shuffles class partitions and lane assignment only, never
  tests within a class; seed printed and stored in results; supervisor-only (plain `dotnet test`
  ordering is the framework's).
- **1.0 hook:** C3's `obj/bobcat/last-run.json` reserves a `seed` field now, so `--repeat` needs no
  format change when this lands.

### 2026-10-16 — C9, vocabulary usage (Jeremy) — **post-1.0, with #552**

- Extend `bobcat vocabulary` (#552): `--usage` lists each step helper's calls, specs and p95
  duration (from `SuiteTiming` / the ledger when a run exists).
- Info diagnostic for a step helper **declared in the spec project's own compilation** that no
  spec calls; shared vocabularies (`WolverineScenario`) are never flagged.
- The authoring skill tells agents to consult `bobcat vocabulary` before writing a new step — the
  part that actually prevents agent-written vocabularies sprawling.

### 2026-10-16 — C10, interop results export (Jeremy) — **post-1.0, Allure first**

- **No Cucumber Messages.** Every result there hangs off a `GherkinDocument`/`Pickle`, so exporting
  means synthesising Gherkin from rendered steps — Gherkin-shaped output, too close to the
  no-Gherkin rule.
- **Allure first, when someone asks:** native steps, parameters and attachments map cleanly onto
  `SpecRender` plus C2's attachments. A one-way projection from the same model the JSON/HTML read.
- CTRF considered (simple, GitHub Actions reporters; a note already in `SupervisorRunPublisher.cs`);
  its step support was not verified. Second in line.

### 2026-10-16 — C11, data-driven examples (Jeremy) — **1.0, inline pipe tables, not CSV**

- **Changed from files to inline tables.** A theory's examples are written as the same pipe-table
  literal Bobcat already uses in C# raw strings (`StepTable`, `TableRunner`, `PropertyCells`) — "the
  inline markdown option". No CSV, no Excel, no external files. Mostly for data-centric tests.
- Shape (attribute name settled at implementation):
  ```csharp
  [BobcatTheory(typeof(ApplyDiscount))]
  [BobcatExamples("""
      | amount | tier | expected |
      | 100    | Gold | 90       |
      | 100    | None | 100      |
      """)]
  public async Task discount_is_applied(decimal amount, Tier tier, decimal expected) { … }
  ```
- The table is a constant, so the generator reads it at compile time: a column naming no parameter
  is a build error (BOBCAT032-style); every row gets C1's identity and is listable in the manifest;
  cells convert through `CellValues` (`NULL`/`EMPTY`/`TODAY+2`/enums/#542 converters).
- Complements `TableRunner`: that is many rows judged inside one spec; this is one spec per row,
  each with its own verdict and identity. Not a Markdown feature set — it is the existing C#
  pipe-table literal. 1.0, alongside C1.

### 2026-10-16 — C12, one behaviour per spec (Jeremy) — **dropped**

- Neither a diagnostic nor guidance. Left to authors. (Sagas, process managers and workflow specs
  legitimately interleave acts and assertions; Bobcat does not police spec shape.)

### 2026-10-16 — C13, strict mode and Pending (Jeremy) — **1.0, part of #545**

- `BOBCAT_LIFECYCLE=strict` means *nothing unfinished*: a failing Acceptance spec **and** every
  `Pending = true` spec fail, the latter as "pending specification in strict mode: <identity>".
  Strict is opt-in per run, so a backlog of pending specs on `main` is unaffected unless someone
  asks that question deliberately.

### 2026-10-16 — C14, published schemas (Jeremy) — **results file 1.0, wire post-1.0**

- **JSON Schema for the results file (#540), 1.0:** generated from the C# records (not hand-written),
  with a test that fails when the committed schema drifts; the Vue HTML page's (#541) TypeScript
  types are generated from the same schema so writer and page cannot disagree; shipped in the
  package and referenced from the file (`"$schema"`).
- **Monitor wire schema: post-1.0**, once #524 (the board's consumer returning to this repository)
  settles who needs a published contract.

### 2026-10-16 — C15, snapshot verification (Jeremy) — **post-1.0, separate package**

- Plan a `Bobcat.Verify` package after 1.0: `SpecAssert.Verified(x)` runs Verify and records one cell,
  linked to the diff on a mismatch. Not in core.
- Trade-offs to state in its docs: a snapshot asserts everything (the opposite of partial objects,
  where a spec names only what it is about), and "accept the new snapshot" is an easy way for an
  agent to turn red into green with nobody deciding to — the docs and the ai-skills entry must
  tell agents never to accept a snapshot without saying what changed.

### 2026-10-16 — C16, practice guidance (Jeremy) — **1.0**

- A section in `critterstack-sdd-bobcat-authoring` plus a short `docs/` page: the **driver
  pattern** for Playwright specs (page mechanics in a driver, `[When]` helpers in business
  language); **declarative over imperative** steps; **the lowest layer that proves the behaviour**
  (store `CritterStackSpec` → message `WolverineSpec` → HTTP → UI only for what is genuinely UI).
  "One behaviour per spec" is deliberately left out (C12). No code.

### 2026-10-16 — DocString check

- Confirmed dead: `ProjectedSpecPreview` is the only producer of a `StepBinding` and always passes
  an empty argument list, so `StepArgumentSource` (incl. `DocString`), the argument bindings and the
  `CommandLineRenderer` arm reading them are Gherkin-era leftovers. Added to #549's cleanup.

### 2026-10-16 — outcome of this pass

- **1.0:** C1 per-row identity + `[BobcatTheory]` + `ParentSpecId`; C2 attachments +
  `IFailureCapture`; C3 `--failed`/`--repeat` + `obj/bobcat/last-run.json` (seed reserved);
  C5 `--slice/--chapter/--domain/--pattern/--trait` + the same coordinates as MTP traits
  (xUnit/TUnit/NUnit, MSTest best-effort); C11 inline pipe-table examples; C13 strict fails Pending
  (on #545); C14 results-file JSON Schema (on #540); C16 practice guidance.
- **Post-1.0:** C4 rules and questions (designed now); C6 sharding; C7 lazy distribution;
  C8 seeded random order; C9 vocabulary usage (with #552); C10 Allure export; C15 `Bobcat.Verify`
  package.
- **Dropped:** C12 one-behaviour policing; Cucumber Messages export (C10).

### 2026-10-16 — C16's docs half becomes a tutorial (Jeremy) — **1.0, #558**

- The docs page for C16 is the **Test Automation Strategy Recommendations** tutorial (#558), drawn
  from Jeremy's 2024 "My Recommendations for a Test Automation Strategy" and 2015 "Succeeding with
  Automated Integration Tests" posts, inlining *some* Bobcat examples and linking to the rest. The
  ai-skills section still ships alongside.

### 2026-10-16/18: decisions taken while driving bobcat-1-0 (Jeremy)

- **The run board and the Event Model canvas live in Bobcat for good** (`bobcat ui`); Stoat is
  coordination only and stopped binding 5525 (stoat#128). Epic #524 closed.
- **Generated contracts are built, not committed** (#604, #609). **CLAUDE.md is an index** of
  `docs/agents/*.md` imports, with no feature write-ups (#605, #606).
- **Every Bobcat bug found while proving Bobcat on real suites** (#565's rollout, dogfooding) goes into
  milestone 1.0 and onto the plan as a node gating rc.1.
- **#562 closes via marten#5690**: Marten's DaemonTests on 1.0.0-alpha.1, merged, *is* the review, so
  it no longer waits for rc.1.
- **#566 ships its design note in 1.0**; building the gate is #729 (Post-1.0).
- **#693, a dependency-light `Bobcat.Supervisor`: no split for 1.0.** Any split still carries a JasperFx floor
  (JasperFx.Testing's recovery-hint attributes, #63), so the documented `VersionOverride` workaround is the answer.
  A `Bobcat.Resilience` package stays a Post-1.0 option if a second build tool hits NU1605.
- **Ledger** (#730, from #565's lessons 1 and 14): key on the **display name** with a uid fallback for
  duplicates, since xUnit v3 uids hash the assembly path and matched 0 tests across machines.
  **CI artifact by default; committing is opt-in**, and retention drops to about 5 runs.
- **`CritterStackScenario.Store` / `CritterStackSpec.Store` is renamed before 1.0**, with no obsolete
  alias (#709): a spec base class's own `Store` hid it with only CS0108.
- **Fixes that belong in WolverineFx.Bobcat go to the Wolverine repo**, with Bobcat documenting the
  limit: #683's Wolverine half (wolverine#5076), #674's timeout-message reschedule (wolverine#5077),
  #670's observer feed (wolverine#5078), plus #675 and #677 as Bobcat docs.
