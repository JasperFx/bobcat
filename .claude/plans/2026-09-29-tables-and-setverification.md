# Handoff — Tables and SetVerification, Gherkin lane done

Rewritten 2026-09-29 at the end of the session that recreated Storyteller's Tables and Sets samples in
the Gherkin lane. The session before it recreated Sentence and Fact grammars in the projected lane;
that part of this file is now history and lives in the commits.

## Where things stand

Branch **`projected-spec-rendering`**, six commits ahead of `main`:

```
4705e2d Make the assertion dialect a seam, with Shouldly as the 1.0 one   ← earlier session
7729921 Project ordinary Shouldly assertions as steps, and evaluate a run of them
68cbfc4 Make failure rendering pluggable, and read an assertion's own message
b4a3dc3 Render a projected specification, and merge the step attributes
<new>   Show which row a set verification is missing
<new>   Recreate Storyteller's Tables and Sets samples, and read every value a step binds
```

**1479 tests green across 12 suites** (`docker compose up -d` first — `Bobcat.CritterStack.Tests`
needs Postgres on 5445). `main` has not been merged in and nobody has opened a PR.

`src/Bobcat/notes.md` is modified in the working tree and was **already** modified before any of this
started. It is not part of it; leave it alone. Same for the untracked `src/Bobcat.EventModel.FrontEnd/`
and `src/Bobcat.Monitor.FrontEnd/`.

## Read first

Two decision records, one per lane, and this file is only the handoff:

- `src/Bobcat.Gherkin.Samples/README.md` — **this** session: the Tables/Sets mapping table, the four
  defects fixed, the gaps still open, and the one rendering decision owed.
- `src/Bobcat.Xunit.Samples/README.md` — the projected lane: Sentences, Facts, projected assertions.

The corpus is `github.com/storyteller/Storyteller` at `master` (v5.4.0) — `src/Samples` and
`src/StoryTeller.Samples`. **Not** `~/code/storyteller`, the abandoned v6 skeleton with no specs in it.
Clone it fresh; the scratchpad copy is gone with the session.

## What this session did

**`src/Bobcat.Gherkin.Samples`** is new — twenty scenarios in two features (`Tables`, `Sets`) with the
fixtures behind them, twelve failing on purpose. A plain `BobcatRunner` console, in `bobcat.slnx`,
collected by nothing:

```bash
dotnet run --project src/Bobcat.Gherkin.Samples/ -- run
dotnet run --project src/Bobcat.Gherkin.Samples/ -- run --feature "Sets"
```

Four defects found and fixed. The README has the detail; in one line each:

1. **A `MISSING` row rendered every column as `-`** — the comparer knew which row was missing and the
   renderer never read it. Both absent-row kinds now carry their values per column, which also
   retired a hack that recovered an extra row's values by re-parsing its own description string.
2. **An extra row did not fail the step** — a green `✓` over a grid with an `EXTRA` row under a red
   scenario. The comparer's failure flag was set for missing rows and wrong cells but not extras.
3. **A value that cannot be read as its parameter's type broke the consumer's build.** The headline,
   and nothing to do with tables: `int` + a non-numeric cell emitted a bare identifier (CS0103), and
   an enum, `DateTime`, `TimeSpan`, `DateOnly`, `TimeOnly`, `DateTimeOffset`, `Uri`, `char` or **any**
   nullable value type got a string literal (CS1503) — in a generated file the author cannot open, in
   a plain `[Given]` sentence. `CellLiterals` is now the single place that decides how a written value
   becomes an argument, covering everything `IsSimpleType` admits, and an unreadable value is the new
   **BOBCAT030** naming the step and parameter. `Bobcat.Generators.Tests/CellLiteralTests` pins both
   sides.
4. **One throwing row erased the whole grid** — the cells reached the result only after the last row,
   so an exception discarded the rows that had already passed. `Apply` now runs in a `finally` in both
   table lanes.

## Next: the projected lane's tables and sets

Same sequencing as before — the Gherkin lane first because the support existed, then the projected
lane, which has the real design question in it. It is unchanged from the last handoff:

A C# test has no trailing `|...|` table. Three options, discussed and not settled:

1. **Repeated calls** — call the row grammar N times. Renders as N steps; the grid is gone.
2. **A collection argument** — `void Sum((int x, int y, int sum)[] rows)`, the helper loops and reports
   one cell per row. `Columns` + `RowIndex` already grid up, so this works today.
3. **Make `Bobcat.StepTable` constructible in C#** — then **one grammar body serves both lanes**: the
   feature file supplies the table, or the caller does. This is the recommendation, and it is the same
   prize the `[FormatAs]` work took for sentences.

For **sets** specifically, C# is arguably better than a table: `SpecAssert.VerifySet(actual, expected,
keyColumns: …)` with expected rows as records, producing the same grid. `SetVerificationComparer` is
pure and reusable, and now carries absent rows' values, so the grid is complete whoever calls it.

Watch for the trap the tuple work hit: a grammar whose expected cells come from the *document* cannot
be called from C# without them, and BOBCAT027 correctly refuses it. A projected table grammar has to
take its expectations as arguments.

## Decisions owed

Newly owed, from this session:

- **Should a throwing table row be an error cell with the remaining rows still evaluated?** Storyteller
  did that and carried on. Bobcat's documented tier says an exception in a step is critical and aborts
  the scenario, which is what still happens — the grid now survives it, but the rows after the bad one
  do not run. For a decision table (a pure function per row) continuing is clearly right; for a
  `[TableGrammar]` sharing state across rows it is less obviously safe. Needs a row-level marker cell
  (`row-error`, beside `missing-row`/`extra-row`) to carry the message, and the renderer to show it.
- **Should a plain `[Table]` step render a grid?** Today it renders one step line per row
  (`✓ Given the invoice details are (row 1)`) and the values it arranged appear nowhere, so the
  specification cannot be read back from its own report. Every other table shape renders a grid, and
  Storyteller rendered all of them. The counterweight is that one step per row is what gives a failing
  row its own line. Changing it changes the shape of every existing report.
- **Ordered sets, primitive sets, inline list captures, column headers/defaults, relative dates** — the
  five gaps in the README's list. Ordering is the biggest: three of the five samples in
  `StoryTeller.Samples/Specs/Sets` are about it, and `[SetVerification]` is key-matched and therefore
  unordered by construction.
- **Tell Stoat that absent rows now carry cells.** `StepFinished.Cells` already carries
  `Name/Status/Expected/Actual/Note/RowIndex` plus `Columns`, so a viewer can now reassemble a grid
  including its missing and extra rows — but only if it knows that a cell named `missing-row` or
  `extra-row` is the row's verdict marker and its siblings are the row's values. Worth a comment on
  **JasperFx/stoat#58**; nothing was posted from here.

Still owed from the earlier session:

- **A passing narrated spec is vacuously green** — three grey `○` and `Rights: 0`. `SuiteTiming` flags
  "asserts nothing" for the Gherkin lane; the projected lane has no equivalent.
- **A comparison assertion's cell label** reads oddly: `should be greater than 10` produces
  `expected '10', got '3'`, where `10` is a bound rather than an expectation.
- **`SpecStep.Log(...)`** does not exist. `StepFinished.Logs` is on the wire and the Gherkin lane fills
  it; a projected step has no way to attach one, so Storyteller's `Context.Reporting.Log` has no
  counterpart. See `BatchProcessGrammar.DoSomethingWorthLogging`.
- **Scenarios sort alphabetically** within a feature in the console report. Source order would be
  better and is a compile-time fact the generator knows and does not register.
- **`main` vs the branch** — nobody has merged or opened a PR.

One stale line worth knowing: `src/Bobcat.Xunit.Samples/README.md` says "twelve of the twenty-five
specifications fail on purpose"; the project now has 28 with 16 failing. The extra failures all read
as the intended deliberate ones — nothing this session touched runs in that lane — so it is the
sentence that is out of date, not the samples.

## How to run everything

```bash
dotnet build bobcat.slnx
docker compose up -d                                        # Postgres on 5445
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
for d in src/*.Tests; do p=$(basename $d); ./$d/bin/Debug/net10.0/$p; done

dotnet run --project src/Bobcat.Gherkin.Samples/ -- run      # the Gherkin corpus, 12 red on purpose
BOBCAT_SPEC_CONSOLE=1 ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples
```

One flake seen once in an earlier session and green on two reruns:
`Bobcat.Tests.Runtime.DockerComposeIntegrationTests.a_recycle_replaces_the_container_and_waits_for_it_again`.
