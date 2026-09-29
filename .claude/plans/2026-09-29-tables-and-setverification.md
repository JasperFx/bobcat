# Handoff — projected specs done, Tables and SetVerification next

Written 2026-09-29, end of the session that recreated Storyteller's Sentence and Fact grammars.

## Where things stand

Branch **`projected-spec-rendering`**, pushed, four commits ahead of `main`:

```
4705e2d Make the assertion dialect a seam, with Shouldly as the 1.0 one
7729921 Project ordinary Shouldly assertions as steps, and evaluate a run of them
68cbfc4 Make failure rendering pluggable, and read an assertion's own message
b4a3dc3 Render a projected specification, and merge the step attributes
```

1461 tests green across 12 suites. `main` has not been merged into or fast-forwarded — Jeremy asked for
the branch, and nobody has opened a PR.

`src/Bobcat/notes.md` is modified in the working tree and was **already** modified before this work
started. It is not part of it; leave it alone.

## Read first

`src/Bobcat.Xunit.Samples/README.md` is the decision record for everything below: the Storyteller→Bobcat
mapping table, what each sample covers, and the gaps still open. This file is only the handoff.

## What exists now, in one pass

Storyteller 5's samples come from `github.com/storyteller/Storyteller` at `master` (v5.4.0) —
`src/Samples` (the documented ones) and `src/StoryTeller.Samples`. **Not** `~/code/storyteller`, which
is the abandoned v6 skeleton with no specs in it at all. Clone it fresh; the scratchpad copy from that
session is gone.

- **`src/Bobcat.Xunit.Samples`** — the recreations. `IsTestProject=false`, because a dozen of them fail
  on purpose; run it directly.
  ```bash
  BOBCAT_SPEC_CONSOLE=1  ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples
  BOBCAT_SPEC_PREVIEW=1  ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples --list-tests
  ```
- **One step-attribute family.** `[Step]` means no keyword and *is* the `StepAttribute` base;
  Given/When/Then/Check derive from it; `[BobcatStep]` is the legacy spelling. All of them work in both
  the Gherkin and the projected lane. `StepAttributes` in the generator is the single recognizer.
- **Two expression syntaxes**, decided per placeholder with the built-in Cucumber word winning:
  `{int}` stays Cucumber, `{sum}` naming a parameter is Storyteller's `[FormatAs]` form. Jeremy prefers
  FormatAs. Mixing is allowed on purpose.
- **Facts** are a `bool`-returning step whose answer is the verdict, `Task<bool>` included. **Named
  tuple returns** compare element by element — the async-safe replacement for `out` parameters.
- **Projected assertions**, opt-in per project via `BobcatProjectAssertions`: a statement-level Shouldly
  call renders as a step and a run of consecutive ones is all evaluated, throwing at the next *action*.
  Shouldly is the 1.0 dialect; `IAssertionDialect` is the seam and carries FluentAssertions' shape in
  its doc comment. `Assert.*` is **not** planned.
- **The wire carries a Storyteller report**: `StepFinished` has `Cells`, `Columns`, `Logs`,
  `Diagnostics`, `ExceptionType`/`StackTrace` and filtered `StackFrames`; `ScenarioStarted` has
  `PlannedSteps`; `StepStarted` has `PlannedStepNumber` and input-value spans.
- Stoat's side is filed as **JasperFx/stoat#58–#62**. #60 has a comment explaining that Bobcat now does
  the stack filtering itself.

## Next task: Tables and SetVerification

Jeremy's sequencing: **the Gherkin lane first**, because the support already exists there, so it is a
rendering review rather than a build. The projected lane is a second pass with a real design question in
it.

### The corpus

From the ST5 clone:

- `src/Samples/Specs/Tables/` — `Before_and_After_Actions`, `Decision_Table`, `Table_with_Options`,
  `Using_[ExposeAsTable]`, `Using_a_Paragraph`
- `src/Samples/Specs/Sets/` — `Arrays`, `Data_Tables`, `Object_Sets`,
  `Set_that_uses_a_non_primitive_type`, `String_Lists`
- `src/StoryTeller.Samples/Specs/Tables/` — `Tables`, `Decision Table`,
  `Table with Optional Columns`, `Boolean Results in a Table`, `Tables with Errors`
- `src/StoryTeller.Samples/Specs/Sets/` — `Ordered Set`, `Unordered Set`, `Unsuccessful Ordering`,
  `SetWithError`, `OrderedStringsSuccess`
- Fixtures: `src/Samples/Fixtures/TableFixture.cs`, `src/Samples/Fixtures/SetsFixture.cs`

`SetsFixture` is the one to read closely — `VerifySetOf(...).Ordered().Comparisons(...)`,
`VerifyStringList`, `NameArrayFixture`'s `{names}` array capture, and `CreateNewObject<T>(...).AsTable(...)`
for arranging the actual rows.

### What Bobcat already has

- `[Table]` on a step — one invocation per row
- `[TableGrammar]` on a class — Before / Row / After envelope, `[ScopePerRow]`
- decision tables — `Row` returning a value with one unbound column, `[Expected("col")]`
- `[SetVerification(KeyColumns = "...")]` — `SetVerificationComparer`, rendered as a real grid
- persistence recipes — `[MartenEntities<T>]`, `[EfCoreEntities<T>]`

The live demo is `ConsolePreview`: `dotnet run --project src/ConsolePreview/ -- run --feature "Inventory"`.
`InventoryFixture.TheInventoryShouldBe` is the `[SetVerification]` example and
`Features/Inventory.feature` has a deliberately wrong scenario.

### Known bug to fix on the way in

A `MISSING` row renders every column as `-`, so you cannot see **which** row was missing even though
the comparer knows:

```
│ 2 │ -       │ -           │ -                        │ MISSING │
```

`SetVerificationComparer.cs:59` builds a `missing-row` cell whose text is
`Expected row not found: {keyDesc}`, and `SpecRender.cs:592` carries it into
`SetVerificationRowRender.Description` — and `CommandLineRenderer.cs:391` never reads `Description`,
emitting `-` for every column instead. Two options, and the second is better:

1. Render `Description` beside or instead of the dashes. One line, no model change.
2. Carry the missing row's **expected cell values per column** so the grid shows them in place, the way
   a wrong cell shows `expected 'x', got 'y'`. Needs the comparer to keep the expected row rather than
   only a description of its key.

Whichever, the wire's `Cells`/`Columns` fields now exist, so Stoat can render the same grid — worth
checking that the missing row survives the trip (it is a cell named `missing-row`, which a viewer has to
know about; that may deserve a note on stoat#58).

### The projected-lane design question

A C# test has no trailing `|...|` table. Three options, discussed and not settled:

1. **Repeated calls** — call the row grammar N times. Renders as N steps; the grid is gone.
2. **A collection argument** — `void Sum((int x, int y, int sum)[] rows)`, the helper loops and reports
   one cell per row. `Columns` + `RowIndex` already grid up, so this works today.
3. **Make `Bobcat.StepTable` constructible in C#** — then **one grammar body serves both lanes**: the
   feature file supplies the table, or the caller does. This is the recommendation, and it is the same
   prize the `[FormatAs]` work just took for sentences.

For **sets** specifically, C# is arguably better than a table: `SpecAssert.VerifySet(actual, expected,
keyColumns: …)` with expected rows as records, producing the same grid. `SetVerificationComparer` is
already pure and reusable.

Watch for the trap the tuple work hit: a grammar whose expected cells come from the *document* cannot be
called from C# without them, and BOBCAT027 correctly refuses it. A projected table grammar has to take
its expectations as arguments.

## Open decisions owed from the last pass

- **A passing narrated spec is vacuously green** — three grey `○` and `Rights: 0`. `SuiteTiming` flags
  "asserts nothing" for the Gherkin lane; the projected lane has no equivalent.
- **A comparison assertion's cell label** reads oddly: `should be greater than 10` produces
  `expected '10', got '3'`, where `10` is a bound rather than an expectation.
- **`SpecStep.Log(...)`** does not exist. `StepFinished.Logs` is on the wire and the Gherkin lane fills
  it; a projected step has no way to attach one, so Storyteller's `Context.Reporting.Log` has no
  counterpart. See `BatchProcessGrammar.DoSomethingWorthLogging`.
- **Scenarios sort alphabetically** within a feature in the console report. Source order would be better
  and is a compile-time fact the generator knows and does not register.
- **`main` vs the branch** — nobody has merged or opened a PR.

## How to run everything

```bash
dotnet build bobcat.slnx
./src/Bobcat.Tests/bin/Debug/net10.0/Bobcat.Tests          # and the other 11 *.Tests
docker compose up -d                                        # Postgres on 5445, for Bobcat.CritterStack.Tests
```

`TESTINGPLATFORM_TELEMETRY_OPTOUT=1` keeps the MTP banner out of captured output.

One flake seen once and green on two reruns:
`Bobcat.Tests.Runtime.DockerComposeIntegrationTests.a_recycle_replaces_the_container_and_waits_for_it_again`.
