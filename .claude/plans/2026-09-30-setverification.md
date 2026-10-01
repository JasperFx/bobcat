# Handoff — working through SetVerification

Written 2026-09-30, at the end of the session that finished Tables. Tables are done in both lanes;
this file is the working document for Sets.

## Where things stand

Branch **`projected-spec-rendering`**, thirteen commits ahead of `main`:

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
```

**1575 tests green across 12 suites** (`docker compose up -d` first — `Bobcat.CritterStack.Tests`
needs Postgres on 5445). `main` has not been merged in and nobody has opened a PR. Issue **#387 is
closed by 342e22e** and Stoat is waiting on a release to bump its pin.

`src/Bobcat/notes.md` is modified in the working tree and was **already** modified before any of this
started. Leave it alone. Same for the untracked `src/Bobcat.EventModel.FrontEnd/` and
`src/Bobcat.Monitor.FrontEnd/`.

## Read first

- `src/Bobcat.Gherkin.Samples/README.md` — the Gherkin lane's decision record: the
  Storyteller→Bobcat mapping for Tables and Sets, the defects fixed, the decisions taken and what
  each cost, and the gaps that remain.
- `src/Bobcat.Xunit.Samples/README.md` — the projected lane, including the table literal.
- `docs/monitor-design.md` — the wire, including `step_progress.Cells` from #387.

The corpus is `github.com/storyteller/Storyteller` at `master` (v5.4.0) — `src/Samples/Specs/Sets`
with `src/Samples/Fixtures/SetsFixture.cs`, and `src/StoryTeller.Samples/Specs/Sets`. **Not**
`~/code/storyteller`, the abandoned v6 skeleton with no specs in it. Clone it fresh.

## What SetVerification does today

`[SetVerification]` on a `[Then]` returning a collection, compared against the step's table:

```csharp
[Then("the ordered details should be")]
[SetVerification(KeyColumns = "Name", Ordered = true)]
public IEnumerable<InvoiceDetail> TheOrderedDetailsShouldBe() => _details;
```

| Capability | State |
|---|---|
| Unordered comparison, matched by `KeyColumns` (all columns when none named) | works |
| Every non-key column compared once the row is matched | works |
| `Ordered = true` — matched first, order of the matches then checked | works |
| A set of plain values: `Column = "Name"` over `IEnumerable<string>` | works |
| Missing row, with its expected values shown in place | works |
| Extra row, with its actual values shown in place, and it fails the step | works |
| Out-of-order row, saying where it actually was | works |
| A row whose comparison throws → `row-error`, other rows still judged | works |
| An expected cell that will not parse → an `invalid` cell naming it | works |
| Tokens and relative dates in an expected cell (`TODAY+2`, `NULL`, `EMPTY`, `"NULL"`) | works |
| A set whose fetch throws | works (critical, as a step's exception is) |
| A non-primitive column (enum) | works |
| BOBCAT014 — a set verification step with no table | works |
| BOBCAT031 — a set of values with no `Column` named | works |

Four row-level marker cells now share one vocabulary, and a viewer has to know all four:
`missing-row`, `extra-row`, `out-of-order` (`SetVerificationComparer`) and `row-error`
(`DecisionTableComparer`). Each sits beside per-column cells carrying the row's values.

Fourteen scenarios in `src/Bobcat.Gherkin.Samples/Features/Sets.feature` cover every
`Specs/Sets/*.md` in both Storyteller sample trees except `Arrays.md` (see the gaps).

## The work: a set verification a C# test can make

This is the one real gap, and the last difference between the lanes.

### The shape, and why — verified, not guessed

A set verification needs two things: the **actual** collection, which the method returns, and the
**expected** rows, which the document supplies. `[SetVerification]` gets the expected rows from the
generator, which is why it cannot be called from C# — the same wall BOBCAT027 correctly refuses the
tuple grammars at.

A step taking a `StepTable` has no such problem, and **already compiles and binds in the Gherkin lane
today** (probed on 2026-09-30 with `GeneratorHarness`: no diagnostics, no compile errors, the
generator emits `f.TheInventoryShouldBe(new StepTable(...))`). The projected lane already renders a
`StepTable` argument as a grid. So the dual-lane shape is reachable now and needs only the comparison:

```csharp
// one body, both lanes
[Then("the inventory should be")]
public void TheInventoryShouldBe(StepTable expected)
    => VerifySet(_inventory.Values, expected, keyColumns: "Sku");
```

```gherkin
Then the inventory should be
  | Sku     | ProductName | Quantity |
  | SKU-001 | Widget      | 90       |
```

```csharp
_inventory.TheInventoryShouldBe("""
    | Sku     | ProductName | Quantity |
    | SKU-001 | Widget      | 90       |
    """);
```

**`[SetVerification]` stays** and is still the canonical declarative form: it is what the preview and
the editor can see, and it carries `KeyColumns`/`Ordered`/`Column` as compile-time facts. The
`StepTable` form is the escape hatch that also works from C#, at the cost of those settings being
arguments rather than declarations. Recommend documenting them that way round rather than replacing
one with the other.

### The step that makes it possible

`SetVerificationComparer.Compare` writes straight onto a `StepResult`: it sets `IsSetVerification`,
`SetVerificationColumns`, `MarkCells` and `MarkFailed`/`MarkSuccess`. A hand-written step has no
`StepResult` — it has a context or a recorder.

**Split it**, the way `TableRunner` is already split:

1. A pure `SetVerificationComparer.Cells(actual, expectedRows, keyColumns, ordered, scalarColumn)`
   returning cells + columns + a verdict — no `StepResult` in the signature.
2. `Compare(..., StepResult)` stays, as the thin adapter the generated code calls, so nothing that
   compiles today changes.
3. `Fixture.VerifySet<T>(...)` reports through the sink `TableRunner.report` already uses:
   `IStepContext.RecordCells` (added by #387's neighbour work) in the Gherkin lane, and
   `ScenarioRecorder`'s open step in the projected lane. That dual sink exists and is tested.

`TableRunner` is the precedent for all of it — engine in `Bobcat.Runtime`, public so a grammar that is
not a `Fixture` can use it, thin `protected` wrappers on `Fixture` for the common case.

### Suggested order

1. Split `Compare` as above, with the existing tests unchanged as the proof nothing moved.
2. `Fixture.VerifySet<T>(IEnumerable<T> actual, StepTable expected, string keyColumns = "",
   bool ordered = false, string column = "")` plus a `TableRunner`-style static for non-fixtures.
3. A Gherkin scenario and a projected test over **one grammar body**, the way
   `Bobcat.Acceptance.Tests/ProjectedTableTests` and `RunTableTests` pin the table equivalents.
4. `Bobcat.Xunit.Samples`: the Sets half of the corpus, which that project's README currently lists
   as not yet covered. `Object_Sets.md` and `Data_Tables.md` are the ones worth recreating — they are
   the four outcomes (happy, extra, missing, mismatch) in one document.
5. Both READMEs.

## Gaps, and what Storyteller did

- **A header for a set's columns.** `[Header]` titles a *parameter*'s column. A set verification's
  columns are the result type's **properties**, so the equivalent is an attribute on the property —
  Storyteller's `_.Compare(o => o.Amount).Header("The Amount")`. Not built. Note it would also want
  to work on the `StepTable` form, where the columns are read reflectively.
- **Inline list captures.** `[FormatAs("The array of names should be {names}")]` with
  `Han, Luke, Chewie` in one cell compared a whole array (`Arrays.md`, the one `Specs/Sets` file not
  recreated). Bobcat has no collection capture. The nearest thing is a set of plain values, which
  needs a table; whether a one-cell list is worth a capture kind of its own is undecided.
- **`MatchOn` vs `KeyColumns`.** Storyteller's `MatchOn(o => o.Amount, o => o.Date)` declared which
  properties were **compared**; Bobcat's `KeyColumns` declares which identify a row and compares
  every column the document names. Bobcat's reading is better — the document decides what it cares
  about — but it means there is no way to say "compare these columns and ignore that one" from the
  fixture. No sample needed it.
- **A set of values and `Ordered` together already work**; `String_Lists.md` and
  `Unsuccessful Ordering.md` are covered. `OrderedStringsSuccess.md` is the same shape passing.

## Decisions owed

Carried forward, none of them blocking the work above:

- **Tell Stoat about the four row-level marker cells.** `StepFinished.Cells` and (now)
  `StepProgress.Cells` carry `Name/Status/Expected/Actual/Note/RowIndex` plus `Columns`, so a viewer
  can reassemble any of these grids — but only if it knows that `missing-row`, `extra-row`,
  `row-error` and `out-of-order` are row verdict markers whose siblings are the row's values. Worth a
  comment on **JasperFx/stoat#58**; nothing has been posted from here.
- **A passing narrated spec is vacuously green** — three grey `○` and `Rights: 0`. `SuiteTiming` flags
  "asserts nothing" for the Gherkin lane; the projected lane has no equivalent.
- **A comparison assertion's cell label** reads oddly: `should be greater than 10` produces
  `expected '10', got '3'`, where `10` is a bound rather than an expectation.
- **`SpecStep.Log(...)`** does not exist. `StepFinished.Logs` is on the wire and the Gherkin lane
  fills it; a projected step has no way to attach one, so Storyteller's `Context.Reporting.Log` has no
  counterpart. See `BatchProcessGrammar.DoSomethingWorthLogging`.
- **Scenarios sort alphabetically** within a feature in the console report. Source order would be
  better and is a compile-time fact the generator knows and does not register.
- **`main` vs the branch** — nobody has merged or opened a PR, and thirteen commits is a lot to land
  in one go.

## How to run everything

```bash
dotnet build bobcat.slnx
docker compose up -d                                        # Postgres on 5445
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
for d in src/*.Tests; do p=$(basename $d); ./$d/bin/Debug/net10.0/$p; done

dotnet run --project src/Bobcat.Gherkin.Samples/ -- run                  # 18 of 30 red on purpose
dotnet run --project src/Bobcat.Gherkin.Samples/ -- run --feature "Sets"
BOBCAT_SPEC_CONSOLE=1 ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples
```

One flake seen once in an earlier session and green on two reruns:
`Bobcat.Tests.Runtime.DockerComposeIntegrationTests.a_recycle_replaces_the_container_and_waits_for_it_again`.
