# Handoff — SetVerification, and what is left after it

Written 2026-09-30, rewritten at the end of the session that closed the Sets gap. Tables and Sets are
now both done in both lanes.

## Where things stand

Branch **`projected-spec-rendering`**, fifteen commits ahead of `main`:

```
b4a3dc3 Render a projected specification, and merge the step attributes        ← session 1
68cbfc4 Make failure rendering pluggable, and read an assertion's own message
7729921 Project ordinary Shouldly assertions as steps, and evaluate a run of them
4705e2d Make the assertion dialect a seam, with Shouldly as the 1.0 one
c44ab4d Handoff for the Tables and SetVerification session
602d7f4 Show which row a set verification is missing, and fail on an extra one  ← session 2
0db3aa0 Recreate Storyteller's Tables and Sets samples, and read every value a step binds
d6d1bf6 A table row is a case of its own: a grid, a failed row, and ordered sets
3a699a5 A table literal in the test, so one grammar body serves both lanes
1cb7275 Handoff for the Tables, Sets and table-literal session
7a56bcf One runtime authority on what a cell means, tokens and relative dates included
44afcfc A table the step runs itself: RunTable, BuildRows, and the hooks that turned out to be code
342e22e StepProgress carries the cells made so far, in both lanes (#387)
1952d54 Handoff for the SetVerification pass
07f91a2 A set verification a C# test can make: VerifySet over a StepTable          ← session 3
4269588 The Sets half of the corpus in the projected lane, and two things it caught
```

**1595 tests green across 12 suites** (`docker compose up -d` first — `Bobcat.CritterStack.Tests`
needs Postgres on 5445). `main` has not been merged in and nobody has opened a PR. Issue **#387 is
closed by 342e22e** and Stoat is waiting on a release to bump its pin.

`src/Bobcat/notes.md` is modified in the working tree and was **already** modified before any of this
started. Leave it alone. Same for the untracked `src/Bobcat.EventModel.FrontEnd/`,
`src/Bobcat.Monitor.FrontEnd/` and the modified `src/Bobcat.Generators.Tests/CellLiteralTests.cs`.

## Read first

- `src/Bobcat.Gherkin.Samples/README.md` — the Gherkin lane's decision record: the
  Storyteller→Bobcat mapping for Tables and Sets, the defects fixed, the decisions taken and what
  each cost, and the gaps that remain.
- `src/Bobcat.Xunit.Samples/README.md` — the projected lane, including the table literal and the
  argument form of a set verification.
- `docs/monitor-design.md` — the wire, including `step_progress.Cells` from #387.

The corpus is `github.com/storyteller/Storyteller` at `master` (v5.4.0) — `src/Samples/Specs/Sets`
with `src/Samples/Fixtures/SetsFixture.cs`, and `src/StoryTeller.Samples/Specs/Sets`. **Not**
`~/code/storyteller`, the abandoned v6 skeleton with no specs in it. Clone it fresh.

## What SetVerification does today

Two forms, both shipped, neither replacing the other.

**Declarative** — `[SetVerification]` on a `[Then]` returning a collection, compared against the
step's table. Canonical where it reaches, because its settings are compile-time facts the preview and
the editor can read, and BOBCAT014/BOBCAT031 are compile errors because of it:

```csharp
[Then("the ordered details should be")]
[SetVerification(KeyColumns = "Name", Ordered = true)]
public IEnumerable<InvoiceDetail> TheOrderedDetailsShouldBe() => _details;
```

**By argument** — a `StepTable` parameter and `VerifySet`. The only form a C# test can call, so it is
what puts one grammar body in both lanes; the price is that `keyColumns`/`ordered`/`column` are
arguments nothing can see before the step runs:

```csharp
[Then("the inventory should be")]
public void TheInventoryShouldBe(StepTable expected)
    => VerifySet(_inventory.Values, expected, keyColumns: "Sku");
```

| Capability | State |
|---|---|
| Unordered comparison, matched by key columns (all columns when none named) | both forms |
| Every non-key column compared once the row is matched | both forms |
| `Ordered` — matched first, order of the matches then checked | both forms |
| A set of plain values under one column | both forms |
| …with the column **inferred** from a one-column table | `VerifySet` only — the table is in view |
| Missing row, with its expected values shown in place | both forms |
| Extra row, with its actual values shown in place, and it fails the step | both forms |
| Out-of-order row, saying where it actually was | both forms |
| A row whose comparison throws → `row-error`, other rows still judged | both forms |
| An expected cell that will not parse → an `invalid` cell naming it | both forms |
| Tokens and relative dates in an expected cell (`TODAY+2`, `NULL`, `EMPTY`, `"NULL"`) | both forms |
| A header row with no rows under it ("the set is empty") names the columns | `VerifySet` only — see the gaps |
| A set whose fetch throws | both forms (critical, as a step's exception is) |
| A non-primitive column (enum) | both forms |
| BOBCAT014 / BOBCAT031 (no table / no column named) | `[SetVerification]` only, by design |
| Callable from a C# test | `VerifySet` only, by design |

Four row-level marker cells share one vocabulary, and a viewer has to know all four: `missing-row`,
`extra-row`, `out-of-order` (`SetVerificationComparer`) and `row-error` (`DecisionTableComparer`).
Each sits beside per-column cells carrying the row's values.

### The shape of the code

`TableRunner` was the precedent and `SetVerificationComparer` now matches it:

- `SetVerificationComparer.Cells(...)` — the comparison, returning a `TableRun` (cells + columns +
  `Succeeded`), with no step of any kind in the signature.
- `Compare(..., StepResult)` — the thin adapter the generated code still calls, so nothing that
  compiled before changed.
- `Verify(actual, StepTable, context, …)` — the argument form, for a grammar that is not a `Fixture`.
- `Fixture.VerifySet<T>(...)` — the `protected` wrapper, sibling of `RunTable` and `BuildRows`.
- **`TableRun.Report(IStepContext?)` is the dual sink both engines report through** —
  `IStepContext.RecordCells` in the Gherkin lane and `ScenarioRecorder`'s open step in the projected
  one, *both*, because a step executing under `BobcatRunner` has a context and no recorder and one
  called from a test has a recorder and no context.

`hasFailure` is gone from the comparer: `TableRun.Succeeded` reads the verdict off the cells, and
`CellCheck` only ever answers success/failed/invalid, so the two readings were provably equal.

### Coverage

- `Bobcat.Acceptance.Tests/VerifySetFixture` is driven from **both** lanes — `VerifySetTests` through
  the generated feature, `ProjectedVerifySetTests` by constructing it and calling the same methods
  with table literals. That is the shape worth keeping: it is the only way the two divergences below
  were visible.
- `Bobcat.Tests/Runtime/SetVerificationComparerTests` covers the new seams, including the
  scalar-column inference and the exception when nothing can infer it.
- Fourteen scenarios in `src/Bobcat.Gherkin.Samples/Features/Sets.feature` and nine in
  `src/Bobcat.Xunit.Samples/Specs/SetSpecs.cs` cover every `Specs/Sets/*.md` in both Storyteller
  sample trees except `Arrays.md` (see the gaps).

## What the dual-lane tests caught

Both of these were invisible from one lane alone, which is the argument for running one grammar twice:

- **A missing row rendered red and left the projected test green.** `RecordedStep.Status` counted a
  `failed` or `invalid` cell and not a `missing` one, and `missing` is exactly what the missing-row
  marker carries. The Gherkin lane's `IStepContext.RecordCells` had always counted it.
- **An absent row rendered as a row of blanks, in the projected lane only.** `rowOfAbsent` took the
  *first* cell per column, and there a step is handed the table as written *before* it compares
  anything — so every column already had an input cell and the comparer's cell was never reached. The
  matched branch next door had already learned this ("LAST wins"); `rowOfAbsent` had not.

Plus one cosmetic one: an extra row's values and a compared cell's actual value went through two
different formatters, so one grid read `expected '2026-09-26'` beside `10/01/2026`.
`CheckFormat.Of`'s own comment exists to prevent exactly that, and the comparer's private copy is now
a call to it.

## Gaps, and what Storyteller did

- **A header-only table is columnless on the generated path.** `Cells` takes the column order
  explicitly now and `Verify` passes `StepTable.Headers`, so "the set should be empty" renders its
  extra rows properly — but `emitSetVerificationStep` still emits only the rows, so a
  `[SetVerification]` step with a header and nothing under it produces a grid with no columns. One
  line in `CodeEmitter` (`step.TableHeaders` is right there) plus a generator test.
- **A header for a set's columns.** `[Header]` titles a *parameter*'s column. A set verification's
  columns are the result type's **properties**, so the equivalent is an attribute on the property —
  Storyteller's `_.Compare(o => o.Amount).Header("The Amount")`. Not built. It would have to work for
  `VerifySet` too, where the columns are read reflectively.
- **Inline list captures.** `[FormatAs("The array of names should be {names}")]` with
  `Han, Luke, Chewie` in one cell compared a whole array (`Arrays.md`, the one `Specs/Sets` file not
  recreated). Bobcat has no collection capture. The nearest thing is a set of plain values, which
  needs a table; whether a one-cell list is worth a capture kind of its own is undecided.
- **`MatchOn` vs `KeyColumns`.** Storyteller's `MatchOn(o => o.Amount, o => o.Date)` declared which
  properties were **compared**; Bobcat's `KeyColumns` declares which identify a row and compares
  every column the document names. Bobcat's reading is better — the document decides what it cares
  about — but it means there is no way to say "compare these columns and ignore that one" from the
  fixture. No sample needed it.
- **`ParseKeyColumns` is a second reading of the generator's own split.** Nothing pins them together
  (unlike `ResourceParsingAgreementTests` / `SliceTagParsingAgreementTests`). The split is "split on
  commas and trim" in both places, so the risk is low; the other option is to have the generator emit
  `SetVerificationComparer.ParseKeyColumns("…")` instead of a materialized array, which collapses
  them to one at the cost of a compile-time fact.

## Decisions owed

Carried forward:

- **Tell Stoat about the four row-level marker cells.** `StepFinished.Cells` and `StepProgress.Cells`
  carry `Name/Status/Expected/Actual/Note/RowIndex` plus `Columns`, so a viewer can reassemble any of
  these grids — but only if it knows that `missing-row`, `extra-row`, `row-error` and `out-of-order`
  are row verdict markers whose siblings are the row's values. Worth a comment on
  **JasperFx/stoat#58**; nothing has been posted from here.
- **A passing narrated spec is vacuously green** — three grey `○` and `Rights: 0`. `SuiteTiming` flags
  "asserts nothing" for the Gherkin lane; the projected lane has no equivalent.
- **A comparison assertion's cell label** reads oddly: `should be greater than 10` produces
  `expected '10', got '3'`, where `10` is a bound rather than an expectation.
- **`SpecStep.Log(...)`** does not exist. `StepFinished.Logs` is on the wire and the Gherkin lane
  fills it; a projected step has no way to attach one, so Storyteller's `Context.Reporting.Log` has no
  counterpart. See `BatchProcessGrammar.DoSomethingWorthLogging`.
- **Scenarios sort alphabetically** within a feature in the console report. Source order would be
  better and is a compile-time fact the generator knows and does not register.
- **`main` vs the branch** — nobody has merged or opened a PR, and fifteen commits is a lot to land
  in one go.

## How to run everything

```bash
dotnet build bobcat.slnx
docker compose up -d                                        # Postgres on 5445
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
for d in src/*.Tests; do p=$(basename $d); ./$d/bin/Debug/net10.0/$p; done

dotnet run --project src/Bobcat.Gherkin.Samples/ -- run                  # 18 of 30 red on purpose
dotnet run --project src/Bobcat.Gherkin.Samples/ -- run --feature "Sets"
BOBCAT_SPEC_CONSOLE=1 ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples  # 24 of 41 red
```

One flake seen once in an earlier session and green on two reruns:
`Bobcat.Tests.Runtime.DockerComposeIntegrationTests.a_recycle_replaces_the_container_and_waits_for_it_again`.

**Relative dates are resolved in UTC.** Running this in the evening US Central, `TODAY` is tomorrow's
date — the Sets grids read `2026-10-01` on 2026-09-30. Not a bug in the samples; worth knowing before
chasing an off-by-one.
