# Storyteller's Tables and Sets samples, as Gherkin specifications

Storyteller 5's `Tables` and `Sets` sample suites, recreated one specification at a time as `.feature`
files against ordinary Bobcat fixtures. The point is **rendering**: those samples were written to show
what each outcome looks like — a wrong cell, a missing row, an extra row, a value that will not parse,
a `Before` that throws — so they are the right corpus for judging what Bobcat's grid should say.

Sources: [`storyteller/Storyteller`](https://github.com/storyteller/Storyteller) at `master`
(version 5.4.0) — `src/Samples/Specs/{Tables,Sets}` with `src/Samples/Fixtures/{TableFixture,SetsFixture}.cs`,
and `src/StoryTeller.Samples/Specs/{Tables,Sets}`. (**Not** `~/code/storyteller`, which is the abandoned
v6 skeleton with no specifications in it.)

The sibling project `Bobcat.Xunit.Samples` does the same job for the **projected** lane and covers
Sentence and Fact grammars. This one is the Gherkin lane, and covers tables and sets.

## Running it

```bash
dotnet build src/Bobcat.Gherkin.Samples/Bobcat.Gherkin.Samples.csproj

dotnet run --project src/Bobcat.Gherkin.Samples/ -- run
dotnet run --project src/Bobcat.Gherkin.Samples/ -- run --feature "Sets"
dotnet run --project src/Bobcat.Gherkin.Samples/ -- list
```

**Twelve of the twenty specifications fail on purpose**, which is why this is a plain `BobcatRunner`
console and not a test project: nothing collects it, and a red run here is the samples working.

## The Storyteller → Bobcat mapping

| Storyteller | Bobcat |
|---|---|
| `[ExposeAsTable("…", "sum")]` + `[return: AliasAs("sum")]` | `[DecisionTable]` + `[Then("…")]` + `[Expected("sum")]` |
| `DecisionTableGrammar` with several computed properties | `[DecisionTable]` on a method with `out` parameters — one column each |
| `this["BuildUser"].AsTable("…").Before(…).After(…)` | `[TableGrammar("…")]` class with `Before` / `Row` / `After` |
| `VerifySetOf(…).MatchOn(o => o.A, o => o.B)` | `[SetVerification(KeyColumns = "A,B")]` on a `[Then]` returning the collection |
| `Paragraph(…).AsTable(…)` | no equivalent, and deliberately none — see the gaps |
| a `bool`-returning `[ExposeAsTable]` | the same `[DecisionTable]`; a `bool` compares like any other value |

Two vocabulary notes. Storyteller's `MatchOn` and Bobcat's `KeyColumns` are the same idea: the columns
that identify a row, with every **other** column compared once the row is matched. And Storyteller's
table titles were prose on the grammar (`AsTable("The Users are")`); in Bobcat the step text *is* the
title, because a step is a sentence in the document.

## What this pass found

Four defects, all fixed here, and the gaps below.

**Fixed — a `MISSING` row rendered every column as `-`.** You could see that a row was missing but not
*which* row, even though the comparer knew: it built a `missing-row` cell describing the key and the
console renderer never read it. The comparer now carries the row's expected values per column beside
that marker (status `ok`, so the row still counts as exactly one failure), the renderer shows them in
place, and an extra row is built the same way — which also retired a hack where `SpecRender` recovered
the extra row's values by **re-parsing its own description string**. A producer that carried only a
description still renders, with a placeholder per column.

```
before                                              after
│ 2 │ -      │ -       │ -     │ MISSING │          │ 2 │ Joplin │ 575 │ 64801 │ MISSING │
```

**Fixed — an extra row did not fail the step.** `Data_Tables.md`'s "Extra Rows Detected from the
Database" rendered `✓ Then the rows should be` over a grid with an `EXTRA` row, under a scenario
marked `FAILED`. The comparer set its failure flag for a missing row and a wrong cell but not for an
extra one, so the step's own verdict disagreed with the error count, the scenario and the exit code. A
set verification says the set is *exactly* this.

**Fixed — a value that cannot be read as its parameter's type broke the consumer's build.** This is the
big one, and it is not about tables at all. `ToCSharpLiteral` emitted the written text for anything
outside a short list of types, so:

- `| a | b | c |` against `int x, int y` emitted the identifiers `a` and `b` → **CS0103: the name 'a'
  does not exist in the current context**
- a `Colour`, `DateTime`, `TimeSpan`, `DateOnly`, `TimeOnly`, `DateTimeOffset`, `Uri`, `char` or **any**
  nullable value type got a string literal → **CS1503: cannot convert from 'string' to …**

— in a generated file the author cannot open, over a step that matched its method perfectly, with no
error anywhere in their own source. Both failed in a plain `[Given]` sentence, not just a table.

The binder already had an opinion about which types a cell can supply (`IsSimpleType`: primitives,
string, **enums**, DateTime/DateOnly/TimeOnly/TimeSpan/DateTimeOffset/Guid/Uri and their nullable
forms) and the emitter simply did not cover it. `CellLiterals` is now the one place that decides, so
those two answers cannot drift: an enum cell becomes `global::Ns.Colour.Blue` (matched without regard
to case; a numeric cell becomes a cast, because a `[Flags]` combination names no member), a date
becomes an invariant-culture `Parse` so the build machine's culture decides nothing, an empty cell
against a nullable value type becomes `null`, and **a value that cannot be read is `BOBCAT030`** naming
the step, the parameter and what was wrong:

```
error BOBCAT030: Feature 'Tables', step 'adding numbers together': the value for 'x'
                 cannot be read — 'a' is not an int
```

Two deliberate details. The diagnostic is collected **at the binding site, as the emitter binds** —
a second validation pass would have re-derived which parameter takes which cell, and drift between
those two opinions shows up as exactly the broken build BOBCAT030 exists to remove. And there is a
floor under it: a binding path that ever escapes the check emits `CellValues.Unreadable<T>(…)`, which
throws a sentence naming the cell, rather than code that does not compile.

Storyteller reported this case as a yellow cell at run time and carried on with the rest of the row,
because it bound values by reflection as the specification ran. Bobcat binds at compile time, so the
row cannot run at all — and the compile-time answer is the better one: the value is wrong in the
document whether or not anybody runs the suite.

**Fixed — one throwing row erased the whole grid.** `Tables.md` has a `3 / 0` row in a table of
divisions. Bobcat reported `! Then dividing numbers — DivideByZeroException` and **no table**: the
cells were gathered into a list and applied to the result only after the last row, so anything that
threw discarded every cell, including the rows that had already passed. `DecisionTableComparer.Apply`
now runs in a `finally` in both table lanes, so the grid shows how far the table got. The failure tier
is unchanged — the exception is still critical and still aborts the scenario. Whether a throwing row
should instead be an error *cell* with the remaining rows still evaluated, as Storyteller did, is a
semantic decision and is left open; see the handoff.

## Gaps, with what Storyteller did

- **Ordered sets.** `VerifySetOf(…).Ordered()` and `VerifyStringList(…).Ordered()` have no equivalent:
  `[SetVerification]` is key-matched and therefore unordered by construction. `Ordered Set.md`,
  `Unsuccessful Ordering.md` and `String_Lists.md` are the samples this costs — three of the five in
  `StoryTeller.Samples/Specs/Sets` are *about* ordering. Storyteller rendered an `Order` column and
  marked the rows whose position was wrong, which is the honest rendering: a set in the wrong order is
  neither missing nor extra.
- **A set of primitives needs a wrapper record.** `SetVerificationComparer` reads a row's columns off
  the actual object's public properties, so an `IEnumerable<string>` yields the columns `Length` and
  `Chars` and every row reads as missing-and-extra. `TheColoursShouldBe` here projects through a
  one-property record to work around it. Storyteller had `VerifyStringList` for exactly this shape.
- **Inline list captures.** `[FormatAs("The array of names should be {names}")]` with
  `Han, Luke, Chewie` in the cell compared a whole array from one capture (`Arrays.md`). Bobcat has no
  collection capture — the closest thing is a table.
- **Column options.** `[Header("Player Name")]` (a column title that is not the parameter name),
  `[DefaultValue]`, `[SelectionValues]`/`SelectionList` and Storyteller's per-table column defaults
  (`-> b = False`, which let a table omit a column entirely) all have no counterpart. A Bobcat column
  binds to the parameter whose **name** it matches, every column must be present, and nothing
  constrains a cell's values. The selection lists mattered most to Storyteller's *editor*; the headers
  and defaults matter to the document.
- **Relative dates.** Storyteller read `TODAY`, `TODAY-1`, `TODAY+2` in any date cell. `SetsFixture`
  here keeps `Date` as a `string` for that reason — with a `DateTime` the samples' own data would be
  BOBCAT030. This is a cell-conversion concern, so `CellLiterals` is now the one place it would go.
- **Paragraphs.** `Paragraph("Divide numbers", …).AsTable(…)` composed a table row out of several
  grammars. Deliberately out of scope: it is the same decision the projected lane took about
  Storyteller's paragraphs.

## One rendering decision worth a second opinion

**A `[Table]` step renders one step line per row, not a grid.** An arrange table reads
`✓ Given the invoice details are (row 1)` three times, and the values it set up are nowhere in the
report — so the specification cannot be read back from its own output, which is the whole point of a
grid. `[TableGrammar]`, `[DecisionTable]` and `[SetVerification]` all render one grid with a verdict
per row; only the per-row `[Table]` step does not, and Storyteller rendered every table as a grid.

The counterweight is that one step per row is what gives a failing row its own line and its own
`✗`. Changing it would change the shape of every existing report, so it is Jeremy's call rather than
a defect.
