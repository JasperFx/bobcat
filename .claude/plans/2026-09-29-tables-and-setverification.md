# Handoff — Tables, Sets and the projected table literal

Rewritten 2026-09-30, end of the session that recreated Storyteller's Tables and Sets samples, took
Jeremy's three decisions off that review, and gave the projected lane a table syntax.

## Where things stand

Branch **`projected-spec-rendering`**, eight commits ahead of `main`:

```
4705e2d Make the assertion dialect a seam, with Shouldly as the 1.0 one    ← earlier session
7729921 Project ordinary Shouldly assertions as steps, and evaluate a run of them
68cbfc4 Make failure rendering pluggable, and read an assertion's own message
b4a3dc3 Render a projected specification, and merge the step attributes
602d7f4 Show which row a set verification is missing, and fail on an extra one
0db3aa0 Recreate Storyteller's Tables and Sets samples, and read every value a step binds
d6d1bf6 A table row is a case of its own: a grid, a failed row, and ordered sets
3a699a5 A table literal in the test, so one grammar body serves both lanes
```

**1532 tests green across 12 suites** (`docker compose up -d` first — `Bobcat.CritterStack.Tests`
needs Postgres on 5445). `main` has not been merged in and nobody has opened a PR.

`src/Bobcat/notes.md` is modified in the working tree and was **already** modified before any of this
started. It is not part of it; leave it alone. Same for the untracked `src/Bobcat.EventModel.FrontEnd/`
and `src/Bobcat.Monitor.FrontEnd/`.

## Read first

Two decision records, one per lane, and this file is only the handoff:

- `src/Bobcat.Gherkin.Samples/README.md` — the Gherkin lane: the Storyteller→Bobcat mapping table for
  Tables and Sets, the four defects this session fixed, the three decisions and what they cost, and
  the gaps that remain.
- `src/Bobcat.Xunit.Samples/README.md` — the projected lane: Sentences, Facts, projected assertions,
  and now the table literal.

The corpus is `github.com/storyteller/Storyteller` at `master` (v5.4.0) — `src/Samples` and
`src/StoryTeller.Samples`. **Not** `~/code/storyteller`, the abandoned v6 skeleton with no specs in it.
Clone it fresh; the scratchpad copy is gone with the session.

## What this session did

**`src/Bobcat.Gherkin.Samples`** is new — twenty-six scenarios in two features (`Tables`, `Sets`) with
the fixtures behind them, sixteen failing on purpose. A plain `BobcatRunner` console, in
`bobcat.slnx`, collected by nothing:

```bash
dotnet run --project src/Bobcat.Gherkin.Samples/ -- run
dotnet run --project src/Bobcat.Gherkin.Samples/ -- run --feature "Sets"
```

Its README is the decision record for everything below.

### Four defects the review found, all fixed

1. **A `MISSING` row rendered every column as `-`** — the comparer knew which row was missing and the
   renderer never read it. Both absent-row kinds now carry their values per column, which also
   retired a hack that recovered an extra row's values by re-parsing its own description string.
2. **An extra row did not fail the step** — a green `✓` over a grid with an `EXTRA` row under a red
   scenario.
3. **A value that cannot be read as its parameter's type broke the consumer's build.** The headline,
   and nothing to do with tables: `int` + a non-numeric cell emitted a bare identifier (CS0103), and
   an enum, `DateTime`, `TimeSpan`, `DateOnly`, `TimeOnly`, `DateTimeOffset`, `Uri`, `char` or **any**
   nullable value type got a string literal (CS1503) — in a generated file the author cannot open, in
   a plain `[Given]` sentence. `CellLiterals` is now the single place that decides how a written value
   becomes an argument, and an unreadable one is **BOBCAT030**.
4. **One throwing row erased the whole grid.** Now fixed twice over — see decision 1 below.

### Jeremy's three decisions, taken 2026-09-30

- **A throwing row is a failed row, and the rest of the table still runs.** The exception becomes a
  `row-error` cell, the row renders `ERROR` with the reason under the grid, and the step is an
  assertion-level failure. The escape hatch is Bobcat's own vocabulary rather than a new one:
  `SpecCriticalException` still aborts the scenario, `SpecCatastrophicException` still stops the
  suite, cancellation propagates (`DecisionTableComparer.IsRowFailure`).
- **A plain `[Table]` step renders a grid.** Both table shapes now go through one emitter
  (`CodeEmitter.emitRowTableStep`), differing only in whether anything is compared. Paid knowingly: a
  failing row gets a grid row rather than its own `✗` line, and a 20-row table is one step rather
  than twenty in every step count and preview.
- **Ordered sets.** `[SetVerification(Ordered = true)]`. **Order is checked after matching, not
  instead of it** — rows are matched by `KeyColumns` and the order of the matches is then verified, so
  an inserted row is one extra row rather than every row after it disagreeing. That is the difference
  between a useful report and a useless one for an event stream with one unexpected event.

Three smaller things decided in the same conversation:

- **A set of plain values names its column**: `[SetVerification(Column = "Name")]` over an
  `IEnumerable<string>` — Storyteller's `VerifyStringList` with the same second argument doing the
  same job. Left unsaid it is **BOBCAT031**.
- **`[Header("Player Name")]`** titles a parameter's column for the document.
- **An optional column is a plain C# optional parameter** and needs no attribute: `Grade grade =
  Grade.Bronze` already says what happens when the column is left out. It also fixed a silent bug —
  a parameter no column named was passed `default(T)`, so a declared default was ignored.
- **`SelectionValues` is declined, not deferred.** It existed for Storyteller's editor, and where it
  constrained a value an enum parameter does it better: a cell outside the list is a BOBCAT030 build
  error naming the alternatives.

### The projected lane got tables

Jeremy's idea, and the answer to the question the last handoff left open: **a table is pipe-delimited
text in the test**, read by `StepTable.Parse` and an implicit conversion from `string`.

```csharp
_roster.TheRosterIs("""
    | player       | position |
    | Nolan Ryan   | Pitcher  |
    | Johnny Bench | Catcher  |
    """);
```

`TheRosterIs(StepTable roster)` is the same method a `.feature` file binds to — **one grammar body,
both lanes** — and both render one grid from one fold, because a table becomes cells carrying a
`RowIndex` plus the column order, which is what a Gherkin `[Table]` step already produced. A markdown
table pastes in unchanged. `Bobcat.Xunit.Samples/Grammars/RosterGrammar.cs` + `Specs/TableSpecs.cs`
are the worked example, and that project's README has the reasoning.

A decision table works the same way: the grammar reports one cell per row through
`SpecAssert.Check(name, actual, expected, rowIndex: i)`, and the comparison supersedes the value the
literal wrote for that column.

## What is left of the two lanes' difference

Sets. `[SetVerification]` needs a collection the method returns and an expected table from the
document; from C# the natural shape is `SpecAssert.VerifySet(actual, expectedTableLiteral, keyColumns,
ordered)` over the same `SetVerificationComparer`, which is pure and now carries absent rows' values
and the ordered check. Nothing is built.

Watch for the trap the tuple work hit: a grammar whose expected cells come from the *document* cannot
be called from C# without them, and BOBCAT027 correctly refuses it. That is why a projected set
verification has to take its expectations as an argument — the table literal is how.

## Decisions owed

- **A header for a set's columns.** `[Header]` titles a *parameter*'s column. A set verification's
  columns are the result type's properties, so the equivalent would be an attribute on the property —
  Storyteller's `_.Compare(o => o.Amount).Header("The Amount")`. Not built; no sample needed it.
- **Inline list captures.** `[FormatAs("The array of names should be {names}")]` with
  `Han, Luke, Chewie` in one cell compared a whole array (`Arrays.md`). Bobcat has no collection
  capture; the closest thing is now a set of plain values.
- **Relative dates.** Storyteller read `TODAY`, `TODAY-1`, `TODAY+2` in any date cell, and the Gherkin
  lane still has `RelativeTimeResolver` for step text. A date *cell* does not go through it —
  `SetsFixture` in the samples keeps its `Date` as a `string` for exactly that reason, since a
  `DateTime` would make the samples' own data BOBCAT030. `CellLiterals` is now the one place it would
  go.
- **Tell Stoat that absent rows and projected tables carry cells.** `StepFinished.Cells` carries
  `Name/Status/Expected/Actual/Note/RowIndex` and `Columns`, so a viewer can reassemble any of these
  grids — but only if it knows that a cell named `missing-row`, `extra-row`, `row-error` or
  `out-of-order` is the row's verdict marker and its siblings are the row's values. Worth a comment on
  **JasperFx/stoat#58**; nothing was posted from here.
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

## How to run everything

```bash
dotnet build bobcat.slnx
docker compose up -d                                        # Postgres on 5445
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
for d in src/*.Tests; do p=$(basename $d); ./$d/bin/Debug/net10.0/$p; done

dotnet run --project src/Bobcat.Gherkin.Samples/ -- run      # the Gherkin corpus, 16 red on purpose
BOBCAT_SPEC_CONSOLE=1 ./src/Bobcat.Xunit.Samples/bin/Debug/net10.0/Bobcat.Xunit.Samples
```

One flake seen once in an earlier session and green on two reruns:
`Bobcat.Tests.Runtime.DockerComposeIntegrationTests.a_recycle_replaces_the_container_and_waits_for_it_again`.
