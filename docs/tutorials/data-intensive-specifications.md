# Data Intensive Specifications

Some specifications are about one value. Others are about *a lot of data* -- dozens of rows set up
before anything happens, and a whole collection verified afterwards instead of a scalar. Written as
one step per row, those specifications stop being readable long before they stop being useful.

Bobcat inherited these mechanisms from Storyteller for exactly this, and they all render as **one
grid per step**:

| | |
|---|---|
| **[A table as input](#a-table-as-input)** | bulk setup -- one step, many rows |
| **[A table the step runs itself](#a-table-the-step-runs-itself)** | objects built from rows, or a method run once per row, with the before/after envelope written in the step's own body |
| **[A decision table](#decision-tables)** | one row per case, inputs and expected outputs in the same table |
| **[A set verification](#set-verification)** | a collection compared against expected rows, matched by key rather than by position |
| **[One object against one row](#one-object-against-one-row)** | a single object's properties checked against the columns a row names |

Every example on this page is a step helper -- a method carrying `[Given]`, `[When]` or `[Then]` --
taking a `StepTable`, called from an ordinary test. They come from `src/Bobcat.Projected.Acceptance.Tests`
and `src/Bobcat.Xunit.Samples`, the second being Storyteller 5's own sample documents recreated as
xUnit v3 tests.

## A table as input

A table arrives as **pipe-delimited text** that `StepTable` reads by an implicit conversion. The step
takes the whole table as one argument:

```csharp
[Given("the roster is")]
internal void TheRosterIs(StepTable roster)
{
    foreach (var row in roster.AsDictionaries())
        _roster.Add($"{row["player"]} ({row.GetValueOrDefault("position", "Outfield")})");
}
```

```csharp
_roster.TheRosterIs("""
    | player       | position |
    | Nolan Ryan   | Pitcher  |
    | Johnny Bench | Catcher  |
    """);
```

A markdown table pastes in unchanged -- the alignment row is recognised and dropped, outer pipes are
optional, cells are trimmed -- so the table in the specification, the table in the pull request and
the table in the test are the same text. Markdown's escaping and inline formatting are deliberately
*not* supported: a cell is the text between pipes.

`StepTable` is the shape a step receives: `Headers`, `Rows`, `AsDictionaries()` (case-insensitive
keys), `Cell(row, header)`, `HasColumn(header)`, `Count`.

The step renders as **one grid, not one step per row**: the table's cells are recorded on the step as
it starts, so a watcher sees the rows while the step is still running, and the grid the step then
reports -- a table it ran, a set it verified -- replaces them rather than arriving beside them.

## A table the step runs itself

Storyteller's batched-setup envelope was
`this["BuildUser"].AsTable("The Users are").Before(…).After(…)` -- a table *declared* around another
grammar. Bobcat's equivalent is `TableRunner`, and the hooks are the lines either side of the call:

```csharp
[Given("the members are")]
internal async Task TheMembersAre(StepTable table)
{
    Log.Clear();                                   // before all rows
    await TableRunner.Run(this, nameof(addMember), table);
    Flushed = string.Join("|", Log);               // after all rows, once
}

private void addMember([Header("Member Name")] string member, Tier tier = Tier.Free)
    => Log.Add($"{member}:{tier}");
```

```csharp
[Given("the subscriptions are")]
internal void TheSubscriptionsAre(StepTable table) => Built = TableRunner.BuildRows<Subscription>(table);

public record Subscription(string Customer, Tier Tier, DateOnly RenewsOn, string Currency = "USD");
```

```csharp
_grammar.TheSubscriptionsAre("""
    | Customer | Tier | RenewsOn |
    | Ada      | Pro  | TODAY+30 |
    | Grace    | Free | TODAY    |
    """);
```

`TableRunner.BuildRows<T>` is Storyteller's `CreateNewObject<T>`: columns bind to the record's
constructor first and then to settable properties, `TODAY+30` is read as a real `DateOnly`, and
`Currency` can be left out of the table entirely because the record declares a default for it.

`TableRunner.Run(target, nameof(rowMethod), table)` calls a method once per row, binding each column to
a parameter **by name**:

- **`[Header("Member Name")]`** titles a column for the table. A heading is prose and a parameter name
  is code, so they need not be the same word.
- **An optional column is a plain C# optional parameter.** `Tier tier = Tier.Free` means the `tier`
  column may be left out of the table entirely, and the declaration already says what happens then.
- **An enum parameter is a selection list.** A cell outside its values fails that row, naming the
  alternatives.
- The row method may be private, and may be async; each row is awaited before the next.

**The before/after hooks are the method body**, and that is the whole design. Storyteller needed
`.Before(...)` and `.After(...)` because the table was declared rather than called; here "before all
rows" is the line above and "after all rows" is the line below -- which is also where a `DbContext` or
a document session gets its single `SaveChangesAsync`. The language already has the feature.

`TableRunner` is one of Bobcat's bounded softenings of "no reflection": a method named by a `string`
cannot be bound at compile time, and naming one is the point -- the row method stays a private detail
of the class instead of a step of its own. `TableRunner.MethodNamed` throws a `SpecCriticalException`
for a name that matches nothing.

## Decision tables

One row per case, with the input columns and the expected-output column in the same table. Hand
`TableRunner.Run` a row method that **returns** a value, and the one column no parameter claims is the
expected output:

```csharp
[Then("doubling gives")]
internal async Task DoublingGives(StepTable table) => await TableRunner.Run(this, nameof(doubled), table);

private int doubled(int input) => input * 2;
```

```csharp
_grammar.DoublingGives("""
    | input | doubled |
    | 2     | 4       |
    | 3     | 7       |
    """);
```

The second row fails in the `doubled` column -- `expected '7', got '6'` -- and the first passes. A
`bool` return is compared like any other value, so a table of yes/no answers needs no special grammar.

Or report each row yourself, which is the shape for a computation that isn't one method call:

```csharp
[Then("adding numbers together")]
internal void AddingNumbersTogether(StepTable sums)
{
    var rows = sums.AsDictionaries();

    for (var i = 0; i < rows.Count; i++)
        SpecAssert.Check("sum", int.Parse(rows[i]["x"]) + int.Parse(rows[i]["y"]),
            int.Parse(rows[i]["sum"]), rowIndex: i);
}
```

The comparison supersedes the value the table wrote for that column, which is why a wrong row reads
`expected '5', got '4'` in the `sum` column rather than echoing the `5` the test typed.

### A row that throws is that row's failure

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

A row of a table is an independent case, not a step in a sequence, so a row that throws fails that
row and **the rest of the table still runs** -- Storyteller's behaviour. Before this, one throwing
row erased the whole grid: the cells were gathered and applied only after the last row, so anything
that threw discarded every cell, including the rows that had already passed.

The step's verdict keeps the difference between a wrong and an exception. A row whose code threw
makes the step `error`. A wrong value, a missing row or an extra row makes it `failed`, because each
of those is the system disagreeing with the table, not the code falling over.

The escape hatch is Bobcat's existing failure vocabulary rather than a new one. A
`SpecCriticalException` still aborts the scenario and a `SpecCatastrophicException` still stops the
suite, so a step that means "stop here" can still say so, and cancellation propagates untouched.
The rows already reached stay on the grid even then.

## Decision tables with out parameters

<!-- JEREMY -->
Storyteller let a decision table grammar declare its outputs as `out` parameters, and it's still the
most natural way to write a table where one set of inputs produces more than one answer. A returned
value only gives you one output column. Out parameters give you as many as you need, and each one is
named right there in the method signature.

Hand `TableRunner.Run` a row method with `out` parameters, and each one is an output column:

```csharp
[Then("the batting lines are")]
internal async Task TheBattingLinesAre(StepTable table) => await TableRunner.Run(this, nameof(battingLine), table);

private string battingLine([Header("At Bats")] int atBats, int hits, int walks,
    out decimal average, [Header("On Base")] out decimal onBase)
{
    average = Math.Round((decimal)hits / atBats, 3);
    onBase = Math.Round((decimal)(hits + walks) / (atBats + walks), 3);

    // The returned value is compared against the one column no parameter names: Verdict.
    return average >= 0.300m ? "All-Star" : "Bench";
}
```

```csharp
await _roster.TheBattingLinesAre("""
    | At Bats | hits | walks | average | On Base | Verdict  |
    | 500     | 150  | 50    | 0.300   | 0.364   | All-Star |
    | 400     | 100  | 40    | 0.250   | 0.318   | Bench    |
    | 600     | 198  | 0     | 0.330   | 0.330   | All-Star |
    """);
```

That's `TableSpecs.a_decision_table_with_out_parameters` in `Bobcat.Xunit.Samples`, and it renders as
one grid with a verdict in each of the `average`, `On Base` and `Verdict` columns of every row.

The rules:

- **The ordinary parameters are the inputs.** They bind from their columns exactly as they do for any
  row method: by name, case-insensitive, or by a `[Header]` title, and an optional parameter's column
  may be left out.
- **Every `out` parameter is an output.** Its column is found the same way an input's is -- its name,
  case-insensitive, or its `[Header]` title -- and after the call its value is compared with that
  row's cell. An out parameter's column is never read as an argument.
- **A returned value is compared against the one column no parameter names**, in or out. Out
  parameter names are always outputs, so they never count toward that "one leftover column" rule.
  With no leftover column, or more than one, the returned value isn't checked.
- **A method can have both**, and you get one judged column per output, as in the example above.
- **An output with no column isn't checked.** Leave `On Base` out of the table and `onBase` is simply
  not compared, the same way an optional input's column can be left out. A table only asserts what it
  writes down.
- **An output cell is compared exactly like a returned value's cell**: the same value parsing, the
  same type-aware comparison, and the same `NULL` and `EMPTY` rules. A wrong value fails that cell
  with `expected '0.320', got '0.318'` and leaves the row's other outputs judged on their own.
- **A row that throws is that row's failure**, as above. Nothing is invented for its output
  columns, and the rest of the table still runs.

## Set verification

A set verification says the collection is **exactly** these rows. Rows are matched by the columns
that identify them, every other column the table names is compared once a row is matched, and order
is not part of the claim unless you say so:

```csharp
[Then("the inventory should be")]
internal void TheInventoryShouldBe(StepTable expected)
    => SetVerificationComparer.Verify(_inventory, expected, keyColumns: "Sku");
```

```csharp
_grammar.TheInventoryShouldBe("""
    | Sku     | ProductName | Quantity |
    | SKU-001 | Widget      | 90       |
    | SKU-002 | Gadget      | 12       |
    """);
```

`keyColumns` is Storyteller's `MatchOn` under another name, comma-separated; leave it empty to match on
every column the table names.

### The four row markers

```
    ✗ Then  the unordered details should be
╭───┬─────────────────────────┬──────────────────────┬────────────┬─────────╮
│ # │ Amount                  │ Date                 │ Name       │ Status  │
├───┼─────────────────────────┼──────────────────────┼────────────┼─────────┤
│ 1 │ expected '11', got '10' │ 2026-09-29 (TODAY-2) │ Socks      │  FAIL   │
│ 2 │ 200                     │ expected …, got …    │ The Pants  │  FAIL   │
│ 3 │ 100                     │ TODAY                │ Sweatpants │ MISSING │
│ 4 │ 100                     │ 2026-10-01           │ The Shirts │  EXTRA  │
╰───┴─────────────────────────┴──────────────────────┴────────────┴─────────╯
```

| | |
|---|---|
| `FAIL` | the row matched and a cell disagrees |
| `MISSING` | the specification describes a row the system never produced -- **shown with the values it expected**, not a row of dashes |
| `EXTRA` | the system produced a row the specification does not describe. This fails the step: a set verification says the set is *exactly* this |
| `ORDER` | with `Ordered = true`, a row that turned up behind one written before it, and where it actually was |

**A mismatch reads as one missing row beside one extra**, not as a row with a wrong cell, whenever
the disagreeing column is a key column -- rows 3 and 4 above are the single `Sweatpants`/`The Shirts`
disagreement. That is `keyColumns` doing its job, and naming fewer of them is what turns a mismatch
back into a cell-level difference.

### Ordered sets, and sets of plain values

```csharp
SetVerificationComparer.Verify(_inventory, expected, keyColumns: "Sku", ordered: true);

// A one-column table over plain values needs no column named
SetVerificationComparer.Verify(_tags, expected);
```

`ordered: true` is one argument on the assertion that already exists, because whether order is part of
the claim is a property of the assertion rather than a different kind of assertion. **Order is checked
after matching, not instead of it:** rows are matched by key as before and the order of the matches is
then verified, so an inserted row is *one extra row* rather than every row after it disagreeing --
which is the difference between a useful report and a useless one for an event stream with one
unexpected event.

For a collection of plain values, the single column is inferred from a one-column table, because there
is only one thing it could be. `column: "Name"` names it explicitly -- Storyteller's
`VerifyStringList(...).Titled(title, "Name")`, with no wrapper record and no second grammar.

### Titling a set's columns

A set's columns are the result type's **properties**, so the property is where `[Header]` goes  -- 
Storyteller's `_.Compare(o => o.Amount).Header("The Amount")`:

```csharp
public record Ledger([property: Header("The Amount")] decimal Amount, string Name);
```

```csharp
_ledger.TheLedgerShouldBe("""
    | The Amount | Name   |
    | 100.50     | Widget |
    """);
```

**The title replaces the name rather than aliasing it**, and that is deliberate: one column with two
spellings is how a grid ends up with two columns for one property. So `keyColumns` names columns as
the *table* writes them -- a titled property is named there by its title. Both readings of "what is
this column called" go through one authority (`Runtime.ColumnNames`), because a column titled one
thing on the way in and another on the way out would be two columns.

### An empty expected set still has headings

A header-only table -- "the set should be empty" -- is a legitimate expectation, and the grid takes its
column order from the table's headers rather than from the first expected row. Before that, such a
step produced a grid with no headings and rendered its extra rows under nothing.

## One object against one row

The mechanisms above are about *many* rows. This one is about a single object, and the columns the
row names are compared against the properties of those names -- Storyteller's `VerifyObject`:

```csharp
[Then("the address should be")]
internal void TheAddressShouldBe(StepTable expected) => PropertyCells.Verify(_address, expected);
```

```csharp
_objects.TheAddressShouldBe("""
    | Address1     | Address2 | City   |
    | 3 1st Street | EMPTY    | Dallas |
    """);
```

**Only the columns the row names are compared.** An address has six fields and a specification that
names three means nothing by the other three, which is the same partial rule set verification and the
Critter Stack vocabulary follow rather than a second convention. Cell tokens read as they do anywhere
else, so `EMPTY` above is the empty string. On a `CritterStackSpec`, `Verify(subject, table)` is the
same check.

It renders as a one-row grid with a verdict per column:

```
    ✗ Then  the address should be
╭───┬─────────────────────┬─────────────────────┬─────────────────────┬────────╮
│ # │ Address1            │ City                │ StateOrProvince     │ Status │
├───┼─────────────────────┼─────────────────────┼─────────────────────┼────────┤
│ 1 │ expected '9 Ninth   │ expected 'Houston', │ expected 'OK', got  │  FAIL  │
│   │ Way', got '2 Second │ got 'Austin'        │ 'TX'                │        │
│   │ Lane'               │                     │                     │        │
╰───┴─────────────────────┴─────────────────────┴─────────────────────┴────────╯
```

### A column may reach into a nested object

A column name containing `.` follows the path, property by property:

```
| Id    | Customer.Name | Customer.Address.City |
| ORD-1 | Hannah        | Austin                |
```

`[Header]` titling applies at **every** segment, so a property renamed for the table stays
addressable nested as well as at the top.

Two failures that look similar and are not:

- **A segment that names no property is `invalid`** -- the specification asked about something that
  does not exist, which is decidable without running anything. The message names what is there *at
  the depth that failed*: `no 'Town' on Address -- it has City, Line1, zip`. The subject's own
  top-level properties would be the least useful half of the sentence, because the typo is in the
  last segment.
- **A segment that is null partway along is `failed`** -- the path is legal and the graph was empty,
  which only run time can tell. The cell reads `expected 'Austin', got 'NULL' (Customer.Address was
  null)`. A null at the *end* of a path is just a value, and `NULL` is a cell expression a
  specification may legitimately assert.

**Collection indexers are not supported**, deliberately: `Items[0].Sku` is a question a set
verification answers properly, matching rows by key columns. A path into a collection would make one
wrong value read as a missing row beside an extra one -- the confusion the next section is about. The
cell says so rather than failing silently. Depth is capped at 8, so a cyclic graph cannot hang a
comparison.

### It is not a set verification of one row

A set matches rows by key columns, so a single wrong value there becomes a missing row beside an
extra one. Here the subject is known and the columns *are* the claim, so a wrong value is one failed
cell. Different question, different comparison -- but the same `CellCheck` underneath, so a property
column and a set column disagree in the same words, and the same `ColumnNames`, so a property titled
by `[Header]` is titled here too.

A column naming no property is an `invalid` cell **at run time**, listing what the type does have,
because the subject is a value the step chooses. Where the type and the table are both constant at the
call site, **BOBCAT032** catches the stale column at build time instead.

## Comparison options

A comparison's options are arguments, on the call that makes it:

| | |
|---|---|
| `SpecAssert.Check(name, actual, expected, tolerance: 0.001)` | compare numerically within an absolute tolerance instead of exact equality. Floating-point verification is where people otherwise give up |
| `CheckOptions { Tolerance, CaseSensitive, Trim, ComparisonType }` | passed as `options:` to `SpecAssert.Check` or `SpecAssert.EventuallyAsync`. `ComparisonType` names an `IValueChecker<T>` of your own, for a type whose equality is domain-specific |

## What a written cell may say

Beyond a plain literal, a cell may say any of these -- and it means the **same thing on both sides of
a table** -- in a row `TableRunner` builds or runs, and in a set verification's expected column:

| | |
|---|---|
| `NULL` | null, where the target allows it |
| `EMPTY` | the empty string, or null for a reference type |
| `TODAY`, `TODAY+2`, `TODAY-1` | a date relative to the run |
| `NOW - 30 minutes`, `TODAY - 1 week` | a relative time. Minutes, hours, days and weeks resolve; **months and years deliberately do not**, because their length depends on which month, and a spec asserting the offset would drift |
| `"NULL"` | the *word* -- a quoted cell is its literal text |

`Bobcat.Runtime.CellValues` is the single authority on what a written cell means.

**A relative time resolves at run time**, because "today" is a fact about the run. A relative token against a `string` is the
*word* -- a table entitled to keep its dates as text says `TODAY` and means it -- and against a number
it is refused as a number, which is the better message.

The grid shows the resolved value with the token as its note:

```
│ 1 │ 10     │ 2026-09-28 (TODAY-2) │ Socks      │   OK   │
```

### A cell that cannot be read

A cell that won't convert to the type it binds to is a `BadCellException`, which sits deliberately
*outside* the `Spec*` tier so it fails its own row and lets the others run:

```
│ 2 │ Nobody      │ Shortstop │ ERROR  │
  row 2: BadCellException: The cell 'Shortstop' could not be read as Position:
          'Shortstop' is not one of Position's values (Pitcher, Outfield, Catcher)
```

A value written in a step helper's *call* -- a literal argument -- is checked at build time instead:
**BOBCAT030** reports one that can't be read as the parameter's type.

## What a grid says on the wire

A step's cells travel on `step_finished` (and, while a long table is still running, on
`step_progress`), and **a cell fills exactly one content field**:

| | |
|---|---|
| `Expected` / `Actual` | a judged cell |
| `Note` | a cell that says a sentence -- the missing / extra / out-of-order markers |
| `Value` | a cell that was **not judged** -- a decision table's input column, an echoed value |

A grid carries **one cell per row and column**. The step is handed the table as written
before it does anything, so the first `step_progress` shows those cells plain, and the grid the step
reports (a table it ran, a set it verified) replaces them rather than arriving beside them. A column
a throwing row never produced is absent from the wire and renders `error`.

`Value` is a fourth field rather than a reuse of `Actual`, and the difference is load-bearing: a cell
with neither an expected nor an actual is how the report decides an input column earns no Status
column at all, so writing an input value into `Actual` would make every input cell look judged.

A cell also carries **what it compared** -- `equals` by default, and one of a closed set otherwise, so
a non-equality assertion cannot state an equality it never checked. Every comparison on this page is
equality, so they all leave it unstated. See
[What a Run Publishes](../monitor-design.md#a-cell-says-exactly-one-thing-issue-396-2026-10-02) and
[A cell says what it compared](../monitor-design.md#a-cell-says-what-it-compared-issue-384).

## Gaps worth knowing about

- **No inline list capture.** Storyteller's `[FormatAs("The array of names should be {names}")]`
  compared a whole array from one cell. The closest thing here is a table, which is a set of plain
  values.
- **No paragraphs.** Storyteller's `Paragraph("Divide numbers", …).AsTable(…)` composed a table row
  out of several grammars. Deliberately out of scope.

## See also

- [Writing Specifications](../marker-steps.md#tables-and-sets-from-a-c-test) -- step helpers, and `SpecAssert`
