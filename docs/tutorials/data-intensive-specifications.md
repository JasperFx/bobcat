# Data Intensive Specifications

Some specifications are about one value. Others are about *a lot of data* — dozens of rows set up
before anything happens, and a whole collection verified afterwards instead of a scalar. Written as
one `Given` line per row, those specifications stop being readable long before they stop being
useful.

Bobcat inherited these mechanisms from Storyteller for exactly this, and they all render as **one
grid per step**:

| | |
|---|---|
| **[A table as input](#a-table-as-input)** | bulk setup — one step, many rows |
| **[A table the step runs itself](#a-table-the-step-runs-itself)** | the same, with the before/after envelope written in the step's own body |
| **[A set verification](#set-verification)** | a collection compared against expected rows, matched by key rather than by position |
| **[A decision table](#decision-tables)** | one row per case, inputs and expected outputs in the same table |
| **[One object against one row](#one-object-against-one-row)** | a single object's properties checked against the columns a row names |

Everything on this page works in **both lanes**. The Gherkin examples come from
`src/Bobcat.Gherkin.Samples`, the C# ones from `src/Bobcat.Xunit.Samples`, and the two projects are
deliberately the same Storyteller documents written twice — which is what keeps the two renderings
honest about each other.

## A table as input

`[Table]` on a step method makes the step take the trailing data table and binds each row's cells to
the method's parameters by **header name**:

```csharp
[Table]
[Given("the roster is")]
public void TheRosterIs([Header("Player Name")] string player, Position position = Position.Outfield)
    => Roster.Add($"{player} ({position})");
```

```gherkin
Given the roster is
  | Player Name  | position |
  | Nolan Ryan   | Pitcher  |
  | Willy Mays   | Outfield |
  | Johnny Bench | Catcher  |
```

Two of the three options Storyteller spelled out are here, and one of them needs no attribute at
all:

- **`[Header("Player Name")]`** titles a column for the document. A heading is prose and a parameter
  name is code, so they need not be the same word.
- **An optional column is a plain C# optional parameter.** `Position position = Position.Outfield`
  means the `position` column may be left out of the table entirely, and the declaration already
  says what happens then — in the one place a reader of the fixture looks. (This also fixed a silent
  bug: a parameter no column named used to be passed `default(T)`, so a declared default was ignored
  and the fixture saw `null` or an enum's zero value.)
- **A selection list is declined, not deferred.** `[SelectionValues(...)]` existed for Storyteller's
  editor, and where it genuinely constrained a value an **enum parameter** now does the job better:
  a cell outside the list becomes a build error naming the alternatives, which a runtime selection
  list never could.

A `[Table]` step renders as **one grid, not one step per row**. It used to emit a step per row, so
an arrange table read `✓ Given the invoice details are (row 1)` three times and the values it set up
appeared nowhere — the specification could not be read back from its own report. The cost, paid
knowingly: a failing row gets its own grid row rather than its own `✗` line, and a 20-row table is
one step rather than twenty in every step count and preview.

### From a C# test

A C# test has no trailing `|...|` block, so a table arrives as **pipe-delimited text** that
`StepTable` reads by an implicit conversion. The step takes the whole table as one argument:

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

A markdown table pastes in unchanged — the alignment row is recognised and dropped, outer pipes are
optional, cells are trimmed — so the table in the specification, the table in the pull request and
the table in the test are the same text. Markdown's escaping and inline formatting are deliberately
*not* supported: a cell is the text between pipes, because that is what a Gherkin cell is, and two
rules for reading a cell is how the lanes would drift.

`StepTable` is the shape a step receives either way: `Headers`, `Rows`, `AsDictionaries()`,
`Cell(row, header)`, `HasColumn(header)`, `Count`.

## A table the step runs itself

Storyteller's batched-setup envelope was
`this["BuildUser"].AsTable("The Users are").Before(…).After(…)` — a table *declared* around another
grammar. Bobcat's equivalents are two methods on `Fixture`, and the hooks are the lines either side
of the call:

```csharp
[Given("the team is")]
public async Task TheTeamIs(StepTable table)
{
    _team.Clear();                            // before all rows
    await RunTable(nameof(addToTeam), table);
    TeamSaved = string.Join("; ", _team);     // after all rows, once
}

private void addToTeam([Header("Player Name")] string player, Position position = Position.Outfield)
    => _team.Add($"{player}:{position}");
```

```csharp
[Given("the invoices are")]
public void TheInvoicesAre(StepTable table) => Invoices = BuildRows<Invoice>(table);

public record Invoice(string Id, decimal Amount, DateOnly DueOn, string Currency = "USD");
```

```gherkin
Given the invoices are
  | Id    | Amount | DueOn   |
  | INV-1 | 100.50 | TODAY   |
  | INV-2 | 200.00 | TODAY+2 |
```

`BuildRows<T>` is Storyteller's `CreateNewObject<T>`: columns bind to the record's constructor
first and then to settable properties, `TODAY+2` is read as a real `DateOnly`, and `Currency` can be
left out of the table entirely because the record declares a default for it.

**The before/after hooks are the method body**, and that is the whole design. Storyteller needed
`.Before(...)` and `.After(...)` because the table was declared rather than called; here "before all
rows" is the line above and "after all rows" is the line below — which is also where a `DbContext`
or a document session gets its single `SaveChangesAsync`. That is the same observation as an
optional column being an optional C# parameter: the language already has the feature.

Everything else matches the generated `[Table]` envelope on purpose — columns bind by name,
`[Header]` renames one, an optional parameter's column may be left out, cells convert the same way,
a row that throws is a failed row with the rest still run, a returned value plus one unclaimed
column is a decision table, and the whole thing renders as one grid. **A table run this way and a
table bound by the generator report the same way over the same document.**

`TableRunner` is the engine, and it is public so a grammar that is not a `Fixture` can use it —
`TableRunner.BuildRows<Signing>(table)` is how the projected lane's plain-class grammars do it. It
is one of Bobcat's bounded softenings of "no reflection": a method named by a `string` cannot be
bound at compile time, and naming one is the point — the row method stays a private detail of the
fixture instead of a step in the document.

### `[TableGrammar]` — the declared form

The envelope can also be a class, which is the right shape when the grammar is shared across
features:

```csharp
[TableGrammar("the users are")]
public class UserTableGrammar
{
    private readonly List<User> _users = new();

    public void Before() => _users.Clear();
    public void Row(string first, string last) => _users.Add(new User(first, last));
    public void After() => saveUsersToTheDatabase(_users);   // once, however many rows
}
```

A fresh instance per execution, so `Before`'s session and `After`'s save share fields. `[Before]`,
`[Row]` and `[After]` override the naming convention; `[ScopePerRow]` gives each row its own DI
scope. Failure tiers are deliberate: a `Before` that throws skips the rows and **still runs
`After`** — the half-finished `Before` is the one that leaves something to clean up — while per-row
failures gather and render the full table.

Note that `[TableGrammar]` steps are invisible to the VS Code Cucumber extension; see
[Integrating Bobcat with Your IDE](ide-integration.md).

See also [Composing Grammar Modules](../composing-grammars.md#building-an-object-from-a-table-row).

## Set verification

A set verification says the collection is **exactly** these rows. Rows are matched by the columns
that identify them, every other column the table names is compared once a row is matched, and order
is not part of the claim unless you say so:

```csharp
[Then("the unordered details should be")]
[SetVerification(KeyColumns = "Name")]
public IEnumerable<InvoiceDetail> TheUnorderedDetailsShouldBe() => _details;
```

```gherkin
Then the unordered details should be
  | Amount | Date    | Name       |
  | 10     | TODAY-2 | Socks      |
  | 200    | TODAY-1 | The Pants  |
  | 100    | TODAY   | The Shirts |
```

The method returns the **actual** collection and the generator supplies the **expected** rows from
the document. `KeyColumns` is Storyteller's `MatchOn` under another name.

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
| `MISSING` | the specification describes a row the system never produced — **shown with the values it expected**, not a row of dashes |
| `EXTRA` | the system produced a row the specification does not describe. This fails the step: a set verification says the set is *exactly* this |
| `ORDER` | with `Ordered = true`, a row that turned up behind one written before it, and where it actually was |

**A mismatch reads as one missing row beside one extra**, not as a row with a wrong cell, whenever
the disagreeing column is a key column — rows 3 and 4 above are the single `Sweatpants`/`The Shirts`
disagreement. That is `KeyColumns` doing its job, and naming fewer of them is what turns a mismatch
back into a cell-level difference.

### Ordered sets, and sets of plain values

```csharp
[SetVerification(KeyColumns = "Name", Ordered = true)]
public IEnumerable<InvoiceDetail> TheOrderedDetailsShouldBe() => _details;

[SetVerification(Column = "Name", Ordered = true)]
public IEnumerable<string> TheNamesShouldBe() => _names;
```

`Ordered = true` is one word on the assertion that already exists, because whether order is part of
the claim is a property of the assertion rather than a different kind of assertion. **Order is
checked after matching, not instead of it:** rows are matched by `KeyColumns` as before and the
order of the matches is then verified, so an inserted row is *one extra row* rather than every row
after it disagreeing — which is the difference between a useful report and a useless one for an
event stream with one unexpected event.

`Column` names the single column for a collection of plain values — Storyteller's
`VerifyStringList(...).Titled(title, "Name")` with the same second argument doing the same job, no
wrapper record and no second grammar. Left unsaid it is **BOBCAT031** at build time, because the old
behaviour was to compare each string against the *properties* of `string` and report every row as
missing *and* extra: a report that describes nothing.

### Titling a set's columns

A set's columns are the result type's **properties**, so the property is where `[Header]` goes —
Storyteller's `_.Compare(o => o.Amount).Header("The Amount")`:

```csharp
public record Ledger([property: Header("The Amount")] decimal Amount, string Name);
```

```gherkin
Then the ledger should be
  | The Amount | Name   |
  | 100.50     | Widget |
```

**The title replaces the name rather than aliasing it**, and that is deliberate: one column with two
spellings is how a grid ends up with two columns for one property. So `KeyColumns` names columns as
the *document* writes them — a titled property is named there by its title. Both readings of "what is
this column called" go through one authority (`Runtime.ColumnNames`), because a column titled one
thing on the way in and another on the way out would be two columns.

### An empty expected set still has headings

A header-only table — "the set should be empty" — is a legitimate expectation, and the grid takes its
column order from the table's headers rather than from the first expected row. Before that, such a
step produced a grid with no headings and rendered its extra rows under nothing.

### From a C# test

`[SetVerification]` is declarative — the generator supplies the expected rows — so a C# test has no
way to hand it an expectation. `VerifySet` is the other half, for a step that takes its table as an
argument:

```csharp
[Then("the inventory should be")]
public void TheInventoryShouldBe(StepTable expected)
    => VerifySet(_inventory.Values, expected, keyColumns: "Sku");
```

```csharp
_sets.TheInventoryShouldBe("""
    | Sku     | ProductName | Quantity |
    | SKU-001 | Widget      | 90       |
    """);
```

The comparison is identical: the same `SetVerificationComparer`, the same key matching, the same
order-after-matching rule, the same four markers, the same grid. Only where the expected rows come
from differs. On a grammar that is not a `Fixture`, call `SetVerificationComparer.Verify(...)`
directly.

**Prefer `[SetVerification]` where it reaches.** `KeyColumns`, `Ordered` and `Column` are
compile-time facts there, which is what lets the preview and the editor read them and what makes
BOBCAT014 (a set verification with no table) and BOBCAT031 compile errors. As arguments, nothing can
see them before the step runs. One thing the argument form does better: a set of plain values needs
no `Column` at all, because the table is in view and has exactly one.

## Decision tables

One row per case, with the input columns and the expected-output columns in the same table.
`[DecisionTable]` plus a return value, and `[Expected]` names the output column:

```csharp
[DecisionTable]
[Then("adding numbers together")]
[Expected("sum")]
public int Sum(int x, int y) => x + y;
```

```gherkin
Then adding numbers together
  | x | y | sum |
  | 1 | 1 | 2   |
  | 3 | 4 | 7   |
  | 4 | 9 | 13  |
```

Two or more computed outputs need `out` parameters, because a return value cannot express them —
and each one is a column compared in its own right:

```csharp
[DecisionTable]
[Then("what's my name?")]
public void WhatsMyName(string firstName, string lastName,
    out string fullName, out string lastNameFirst)
{
    fullName = $"{firstName} {lastName}";
    lastNameFirst = $"{lastName}, {firstName}";
}
```

A `bool` return is compared like any other value, so a table of yes/no answers needs no special
grammar. `preview` names the expected-output cell each parameter came from, which is the fastest way
to see that a column bound where you thought it did.

From a C# test, the grammar reports one cell per row itself:

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

The comparison supersedes the value the literal wrote for that column, which is why a wrong row
reads `expected '5', got '4'` in the `sum` column rather than echoing the `5` the test typed.

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
row and **the rest of the table still runs** — Storyteller's behaviour. Before this, one throwing
row erased the whole grid: the cells were gathered and applied only after the last row, so anything
that threw discarded every cell, including the rows that had already passed.

The escape hatch is Bobcat's existing failure vocabulary rather than a new one. A
`SpecCriticalException` still aborts the scenario and a `SpecCatastrophicException` still stops the
suite, so a fixture that means "stop here" can still say so, and cancellation propagates untouched.
The rows already reached stay on the grid even then.

## One object against one row

The three mechanisms above are about *many* rows. This one is about a single object, and the columns
the row names are compared against the properties of those names — Storyteller's `VerifyObject`:

```csharp
[Then("the address should be")]
public void TheAddressShouldBe(StepTable expected) => VerifyObject(_address, expected);
```

```gherkin
Then the address should be
  | Address1     | Address2 | City   |
  | 3 1st Street | EMPTY    | Dallas |
```

**Only the columns the row names are compared.** An address has six fields and a specification that
names three means nothing by the other three, which is the same partial rule set verification and the
event-store grammars follow rather than a second convention. Cell tokens read as they do anywhere
else, so `EMPTY` above is the empty string.

From a C# test, where the grammar is a plain class rather than a `Fixture`, the static is
`PropertyCells.Verify(subject, expected)` — the same split `VerifySet` and `RunTable` have.

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

That grid is also what the two shipped event-store assertions now render —
`Then the {readmodel} read model contains` and `Then the {document} with id {string} has`. They used
to flatten the whole comparison into one exception message:

```
AppointmentsQueue read model did not match: AwaitingConfirmation: expected 0, was 1; Confirmed: expected 0, was -1
```

Three cells green, two red, rendered as a sentence a reader has to parse.

### It is not a set verification of one row

A set matches rows by key columns, so a single wrong value there becomes a missing row beside an
extra one. Here the subject is known and the columns *are* the claim, so a wrong value is one failed
cell. Different question, different comparison — but the same `CellCheck` underneath, so a property
column and a set column disagree in the same words, and the same `ColumnNames`, so a property titled
by `[Header]` is titled here too.

### The one family with no declarative twin

Everywhere else the declarative form is canonical where it reaches — `[SetVerification]` over
`VerifySet`, `[Table]` over `RunTable` — because those carry settings that are compile-time facts the
preview and the editor can read. This one has **nothing to configure**: the columns come from the
table and the subject from the method, so an attribute would carry no information and buy nothing.

One consequence follows from that rather than being a separate decision: a column naming no property
is an `invalid` cell **at run time**, listing what the type does have. It cannot be a compile-time
diagnostic, because the generator would need the subject's type in view and in this form the subject
is a value the step chooses.

## Comparison options

| | |
|---|---|
| `[Expected("column")]` | names the expected-output column for a return value or an `out` parameter |
| `[Approx(0.001)]` | compares numerically with an absolute tolerance instead of exact equality. Floating-point set verification is where people otherwise give up |
| `[Comparison(typeof(MyChecker))]` | a comparison of your own, for a type whose equality is domain-specific |

All three go on the method, a parameter or a property, so a tolerance can be stated once for a whole
grammar or per column.

## What a written cell may say

Beyond a plain literal, a cell may say any of these — and it means the **same thing on both sides of
a table**, in a `[Table]` step's input column, a set verification's expected column, a `RunTable`
row, and a table literal in a C# test:

| | |
|---|---|
| `NULL` | null, where the target allows it |
| `EMPTY` | the empty string, or null for a reference type |
| `TODAY`, `TODAY+2`, `TODAY-1` | a date relative to the run |
| `NOW - 30 minutes`, `TODAY - 1 week` | a relative time. Minutes, hours, days and weeks resolve; **months and years deliberately do not**, because their length depends on which month, and a spec asserting the offset would drift |
| `"NULL"` | the *word* — a quoted cell is its literal text |

This used to be true on the expected side only: `TODAY+2` worked where a specification *asserted* a
date and was a build error where it *supplied* one — the same word meaning two things in one
document. `Bobcat.Runtime.CellValues` is now the single runtime authority on what a written cell
means.

**A relative time resolves at run time, never at build time.** The generator emits a call rather than
a computed date: "today" is a fact about the run, and a build cached overnight would hand every later
run yesterday's date with nothing in the report to say so. A relative token against a `string` is the
*word* — a table entitled to keep its dates as text says `TODAY` and means it — and against a number
it is refused as a number, which is the better message.

The grid shows the resolved value with the token as its note:

```
│ 1 │ 10     │ 2026-09-28 (TODAY-2) │ Socks      │   OK   │
```

### A cell that cannot be read

A cell that will not convert to the type of the parameter it binds to is **BOBCAT030**, an error, at
build time:

```
error BOBCAT030: Feature 'Tables', step 'adding numbers together': the value for 'x'
                 cannot be read — 'a' is not an int
```

Storyteller reported this as a yellow cell at run time and carried on with the rest of the row,
because it bound values by reflection as the specification ran. Bobcat binds at compile time, so the
row cannot run at all — and the compile-time answer is the better one: the value is wrong in the
document whether or not anybody runs the suite.

Before BOBCAT030 existed, the generator emitted the cell's text and the **consumer's** build failed
with `CS0103: the name 'a' does not exist in the current context` at a line inside a generated file
the author cannot open, over a step that matched its method perfectly.

Where the binding happens at run time instead — a `RunTable` row, a table literal, `BuildRows<T>` —
the same bad cell is a `BadCellException`, which sits deliberately *outside* the `Spec*` tier so it
fails its own row and lets the others run:

```
│ 2 │ Nobody      │ Shortstop │ ERROR  │
  row 2: BadCellException: The cell 'Shortstop' could not be read as Position:
          'Shortstop' is not one of Position's values (Pitcher, Outfield, Catcher)
```

## What a grid says on the wire

A step's cells travel on `step_finished` (and, while a long table is still running, on
`step_progress`), and **a cell fills exactly one content field**:

| | |
|---|---|
| `Expected` / `Actual` | a judged cell |
| `Note` | a cell that says a sentence — the missing / extra / out-of-order markers |
| `Value` | a cell that was **not judged** — a decision table's input column, an echoed value |

`Value` is a fourth field rather than a reuse of `Actual`, and the difference is load-bearing: a cell
with neither an expected nor an actual is how the report decides an input column earns no Status
column at all, so writing an input value into `Actual` would make every input cell look judged. See
[What a Run Publishes](../monitor-design.md#a-cell-says-exactly-one-thing-issue-396-2026-10-02).

## Gaps worth knowing about

- **No inline list capture.** Storyteller's `[FormatAs("The array of names should be {names}")]`
  compared a whole array from one cell. The closest thing here is a table, which is now a set of
  plain values.
- **No paragraphs.** Storyteller's `Paragraph("Divide numbers", …).AsTable(…)` composed a table row
  out of several grammars. Deliberately out of scope.
- **No per-table value override.** Storyteller's `-> b = False` fixed a value for every row of one
  table. It has no Gherkin spelling and is not built.

## See also

- [Specs from tests you already have](../marker-steps.md#tables-and-sets-from-a-c-test) — the same four mechanisms from the C# side
- [Composing Grammar Modules](../composing-grammars.md) — sharing a table grammar across features
- [Integrating Bobcat with Your IDE](ide-integration.md) — which of these the editor can and cannot see
