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

**Sixteen of the twenty-six specifications fail on purpose**, which is why this is a plain `BobcatRunner`
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

**Fixed — one throwing row erased the whole grid, and stopped the table.** `Tables.md` has a `3 / 0`
row in a table of divisions. Bobcat reported `! Then dividing numbers — DivideByZeroException` and
**no table**: the cells were gathered into a list and applied to the result only after the last row,
so anything that threw discarded every cell, including the rows that had already passed.

A row of a table is an independent case, not a step in a sequence, so **a row that throws is now that
row's failure and the rest of the table still runs** — Storyteller's behaviour, decided 2026-09-30.
The exception becomes a `row-error` cell, the row renders as `ERROR` with the reason under the grid,
and the step is an assertion-level failure the scenario carries on from:

```
╭───┬────┬───┬──────────┬────────╮
│ # │ x  │ y │ quotient │ Status │
├───┼────┼───┼──────────┼────────┤
│ 1 │ 10 │ 5 │ 2        │   OK   │
│ 2 │ 3  │ 0 │ !        │ ERROR  │
│ 3 │ 9  │ 3 │ 3        │   OK   │
╰───┴────┴───┴──────────┴────────╯
  row 2: DivideByZeroException: cannot divide by zero
```

The escape hatch is Bobcat's own failure vocabulary rather than a new one: a `SpecCriticalException`
still aborts the scenario and a `SpecCatastrophicException` still stops the suite, so a fixture that
means "stop here" can still say so, and cancellation propagates untouched
(`DecisionTableComparer.IsRowFailure`). `Apply` also runs in a `finally`, so even a critical stop
leaves the rows it reached on the grid.

## Since the review: the three decisions, and what they cost

Decided 2026-09-30, all three built and proven above.

**A `[Table]` step renders a grid.** It used to emit one `DelegateExecutionStep` per row, so an
arrange table read `✓ Given the invoice details are (row 1)` once per row and the values it set up
appeared nowhere — the specification could not be read back from its own report. Both table shapes
now go through one emitter (`CodeEmitter.emitRowTableStep`), differing only in whether anything is
compared, which is also how they came to share per-row progress reporting and per-row failure
handling. The cost, paid knowingly: a failing row no longer gets its own `✗` line, it gets its own
grid row; and a 20-row table is one step rather than twenty in every step count and preview.

**Ordered sets.** `[SetVerification(Ordered = true)]` — one word on the assertion that already
exists, because whether order is part of the claim is a property of the assertion rather than a
different kind of assertion. **Order is checked after matching, not instead of it:** rows are matched
by `KeyColumns` as before and the order of the matches is then verified, so an inserted row is one
extra row rather than every row after it disagreeing — which is the difference between a useful
report and a useless one for an event stream with one unexpected event. A row that turns up behind
one written before it is an `out-of-order` cell, renders as `ORDER`, and says where it actually was.

**A set of plain values names its column.** `[SetVerification(Column = "Name")]` over an
`IEnumerable<string>`, which is Storyteller's `VerifyStringList(...).Titled(title, "Name")` with the
same second argument doing the same job — no wrapper record and no second grammar. Left unsaid it is
**BOBCAT031** at build time, because the old behaviour was to compare each string against the
properties of `string` and report every row as missing *and* extra, a report that describes nothing.

**Column options: two of the three.** `[Header("Player Name")]` on a parameter titles its column for
the document, because a heading is prose and a parameter name is code. An **optional column** is a
plain C# optional parameter — `Grade grade = Grade.Bronze` — and needs no attribute at all: the
declaration already says what happens when the column is left out, in the one place a reader of the
fixture looks. That also fixed a silent bug, since a parameter no column named was passed
`default(T)`, so a declared default was ignored and the fixture saw `null` or the enum's zero value.
Storyteller's per-table override (`-> b = False`, which fixed a value for every row of one table) has
no Gherkin spelling and is not built.

**`SelectionValues` is declined, not deferred.** It existed for Storyteller's editor, and where it
genuinely constrained a value an enum parameter now does the job better: `Position position` makes a
cell outside the list a BOBCAT030 build error naming the alternatives, which a runtime selection list
never could.

## Gaps that remain

- **Inline list captures.** `[FormatAs("The array of names should be {names}")]` with
  `Han, Luke, Chewie` in the cell compared a whole array from one capture (`Arrays.md`). Bobcat has no
  collection capture — the closest thing is a table, which is now a set of plain values.
- **A header for a set's columns.** `[Header]` titles a *parameter*'s column. A set verification's
  columns are the result type's properties, so the equivalent alias would be an attribute on the
  property — Storyteller's `_.Compare(o => o.Amount).Header("The Amount")`. Not built; no sample
  needed it once the document could name the columns itself.
- **Relative dates.** Storyteller read `TODAY`, `TODAY-1`, `TODAY+2` in any date cell. `SetsFixture`
  here keeps `Date` as a `string` for that reason — with a `DateTime` the samples' own data would be
  BOBCAT030. This is a cell-conversion concern, so `CellLiterals` is now the one place it would go.
- **Paragraphs.** `Paragraph("Divide numbers", …).AsTable(…)` composed a table row out of several
  grammars. Deliberately out of scope: the same decision the projected lane took about
  Storyteller's paragraphs.

## Where the two lanes still differ

The projected lane has no table syntax yet, so none of this is reachable from a C# test. The plan of
record is a pipe-delimited table literal in the test itself, which would let one grammar body serve
both lanes — the feature file supplies the table, or the caller does. See the handoff.
