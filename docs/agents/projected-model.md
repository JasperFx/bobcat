# The projected model: pipeline, steps, tables, partial objects, comparison

## Architecture

### The projected pipeline

An ordinary test calls step helpers → **Bobcat.Generators** (compile time) intercepts those calls
and reads the test's marker comments → the runner adapter opens a recording per test →
**`ScenarioRecorder`** (run time) records steps, cells and verdicts → **`SpecRender`** → console,
JSON report, the platform's own test output, and the monitor wire.

- **Step interceptors** (`StepInterceptors`): a call to a method carrying a step attribute becomes a
  recorded step, and a `SpecAssert` inside it records onto that step. Projected Shouldly assertions
  (`ProjectedAssertions`) are the opt-in second source.
- **Marker comments and declared values** (`MarkerCommentSpecs`, `DeclaredValues`): `// Given …`
  comments in a test body are steps too; `DeclaredSteps` holds what each test declares, keyed by
  spec identity.
- **Specification bindings** (`SpecificationManifest`, `MarkerSpecNaming`): one identity per
  `[BobcatSpec]`/`[BobcatScenario]` test, the Event Model fold (`EventModelEmitter`) and the spec
  manifest all read the same names.
- **Runner adapters** (`Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit`, `Bobcat.MSTest`) open and
  close the recording; core names none of their types.

User-facing docs: `docs/marker-steps.md`, `docs/tutorials/specifications-with-code.md`.

### Step attributes (`src/Bobcat/Attributes.cs`)

`[Given]`, `[When]`, `[Then]`, `[Check]` and the keywordless `[Step]` are **one family** —
`StepAttribute` is the base, and a project's own attribute deriving from `GivenAttribute` inherits
the keyword (read off the attribute's type, never by instantiating it). An expression is a
Storyteller-style named template: a placeholder binds **by parameter name only** (`{sum}`).
Cucumber type captures (`{int}`, `{word}`) left with Gherkin (#471, #602) and are BOBCAT027,
rendered literally. `[BobcatStep]` (`src/Bobcat/MarkerSteps/`) is the same family
for a shared helper.

- **`[Check]` (not `[Fact]`) for boolean assertions** — avoids the xUnit collision.
- **`[Header("…")]`** on a row-method parameter or a compared property names its column. The title
  **replaces** the name; it is not an alias.
- **`Bobcat.StepTable`** — a step parameter receiving a table literal whole (headers + rows), for
  `TableRunner`, set verifications and partial-object tables. Never resolved from DI.

### Tables, sets and objects (`src/Bobcat/Runtime/`)

Public statics, so any projected grammar — a plain class — reaches one engine:

| | |
|---|---|
| `TableRunner.Run(this, nameof(rowMethod), table)` | Storyteller's `AsTable(...).Before(...).After(...)`, where the hooks are the lines either side of the call |
| `TableRunner.BuildRows<T>(table)` | Storyteller's `CreateNewObject<T>` |
| `SetVerificationComparer.Verify(actual, expected, keyColumns:, ordered:, column:)` | set verification |
| `PropertyCells.Verify(subject, expected)` | Storyteller's `VerifyObject` / `CheckPropertyGrammar` (issue #395) |
| `ObjectSetVerification.Verify(...)` | sets of objects and partial objects (below) |

**`TableRunner` is a decision table when it has outputs.** Columns bind to the row method's
parameters by name (`[Header]` title, else the name, case-insensitive); an optional parameter's
column may be left out; cells convert through `CellValues`. **Each `out` parameter is an output
column (bobcat#483)**, compared after the call exactly as a returned value is; a returned value is
compared against the one column no parameter names, in or out — with none, or more than one, it is
not checked. A row that throws is a `row-error` cell and the remaining rows still run
(`DecisionTableComparer.IsRowFailure`); `SpecCriticalException`, `SpecCatastrophicException` and
cancellation are not row failures. Naming the row method by string is a bounded softening of "no
reflection", beside `RecordBuilding` and the store conventions.

**A domain type in a cell (bobcat#542)** reads through `CellValues.Register<T>(Func<string, T>)`, then
`IParsable<T>`, then a string `TypeConverter`, then a public `ctor(string)` (resolved once per type);
a registration wins over the built-in readers too, and never sees `NULL`/`EMPTY`/relative times/quotes.
`FallbackChecker` (the expected side's last resort) reads through `CellValues`, so both sides agree.
Service-backed converters (Storyteller's `IRuntimeConverter`) are deferred.

A column naming no property is an `invalid` cell **at run time**, unless the table is a constant the
generator can check — see BOBCAT032 below.

`PropertyCells` is general-purpose; it is **not** a set verification of one row, because a set
matches by key columns so one wrong value becomes a missing row beside an extra one, where here the
subject is known and the columns *are* the claim.

**A column may be a dotted path into a nested object (issue #411).** `Customer.Address.City` resolves
left to right over public instance properties **and fields** (bobcat#548 — partial objects already
addressed fields, so a property check now reads what a `Specify<T>()` can name; a property wins a name
a field also claims), with `[Header]` titling applied **per segment** so a
renamed property stays addressable at every depth. Put in the shared lookup rather than in any one
grammar because several callers share it — `PropertyCells.Verify` and the Wolverine side's event and
HTTP-response assertions (wolverine#4835/#4836) among them — and solving it in one would leave the
others with a different rule for what a column name means. Three decisions worth keeping:

- **A missing segment is `invalid`; a null partway along is `failed`.** The first is the
  specification asking about something that does not exist, decidable without running anything; the
  second is the subject disagreeing, decidable only at run time. Collapsing them would make a legal
  spec over an empty graph read as an authoring mistake. A null at the *end* of a path is an
  ordinary value — `NULL` is a cell expression a spec may assert.
- **`invalid` names what is there at the depth that failed** — `no 'Town' on Address — it has City,
  Line1, zip` — not the subject's own top-level properties, which is what a naive extension prints
  and is the least useful half of the sentence.
- **Collection indexers are refused with a message, not supported.** `Items[0].Sku` is a set
  verification's question: a set matches by key columns, where a path into a collection makes one
  wrong value read as a missing row beside an extra one — the exact confusion the paragraph above
  warns about. Depth is capped (`PropertyCells.MaxDepth`) so a cyclic graph cannot hang a comparison.

### Partial objects: `Specify<T>()` and partial tables (`src/Bobcat/Partial/`, bobcat#416/#417)

A spec names only the members it is about. A **partial object** (`IPartialObject`) is a type plus
an ordered list of `SpecifiedValue(Path, Value, IsText)` — typed values from code, or cell text from
a table, read later with `CellValues.Read` so `NULL`/`EMPTY`/relative times mean what they mean in
every other table. Three ways to write one:

- `Specify<T>().With(x => x.Prop, value)` (`using static Bobcat.Specifications;`). Nested:
  `.With(x => x.Address.City, …)` or `.With(x => x.Address, Specify<Address>()…)`.
  `.With(nameof(T.Prop), value)` for a string path. **Refactor friendly by construction** — teach
  this, and `nameof` in table headers, over bare strings.
- `PartialObjects.FromTable(type, table)`: a horizontal table is one object per row; a vertical `|
  field | value |` table (`PartialObjects.IsVertical`) is exactly one. **A blank cell is "not
  specified"** (so one table can carry several types under a union header); `EMPTY` is the empty
  string.
- `PartialObjects.FromCells(type, row)`.

**Building** (`ObjectConstruction`) — constructor first, then members:

1. Of the public constructors that can reach every specified member (by a parameter, or by a
   settable/`init` member afterwards), take the one binding the most, then the one leaving fewest
   parameters to fill, then the shortest. Structs also get their implicit constructor.
2. Unbound parameters take their C# default, else the fill policy.
3. Remaining specified members are assigned through setters or `init` accessors (reflection may call
   `init`) — the gap `RecordBuilding` has: it sets nothing once a constructor binds.
4. A `required` member nothing set, and a non-nullable reference member the type's own initializers
   left null, get the fill policy's value. **A member the type initialized itself is left alone** —
   that default is the author's.
5. Spec defects throw `SpecCriticalException` naming the member: matches nothing (lists what the
   type has), read-only and taken by no constructor, specified both whole and by members, an index
   path.

**Unspecified values go through `IUnspecifiedValues`**; `PredictableValues` is the default: null
where nullable (NRT annotations; *unknown* — F#, oblivious code — counts as non-nullable), `""`,
empty collections, `None`, **a fresh `Guid`** (`Guid.Empty` collides identities), the Bobcat clock's
now, an enum's first value, `default` otherwise; any other reference type is built empty, and a
type already under construction is left null so a cycle terminates. **bobcat#421 settled the
policy on 2026-10-08 (option 2, Storyteller-style declared defaults):** `DeclaredDefaults`
(`For<Order>(x => x.Currency, "USD")`, keyed on the member's declaring type so it applies wherever
that type is built, nested included) sits over `PredictableValues` as its fallback, set process-wide
through `PartialObjects.UnspecifiedValues` or per spec. Bogus/AutoBogus was declined: random values
make specs unreadable and failures unreproducible.

**F# without FSharp.Core** (`FSharpShapes`, by type name): records and `[<CLIMutable>]` go through
the constructor; `option`/`voption` (blank or `NULL` cell = `None`; an inner value is wrapped in
`Some`); `list`/`Set` (comma-separated cell or any enumerable); `Map` (empty only); a single-case
union with one field (`OrderId of Guid`) is built from its wrapped value. Multi-case unions are not
supported yet. `src/Bobcat.Tests.FSharpTypes` holds real compiler-output shapes plus F#-authored
`Specify` calls — test new shapes there, never with C# imitations.

**Matching (#418, `PartialMatching`, `ObjectSetVerification`).** `PartialMatching.Differences(actual,
expected)` judges a partial object on **only** the members it names (cell text through `CellCheck`,
typed values structurally), an `IExpectedValue` (`Expect.Value(x).Ignoring(...)`) on everything but
its ignored members, a plain object on everything. `ObjectComparison`/`Expect`/`ExpectedValue` live
here now (moved from WolverineFx.Bobcat, wolverine#4835) — one comparison. F# options and single-case
unions compare by what they hold (`ObjectComparison.Unwrap`). `ObjectSetVerification.Verify(actual,
expected, noun, SetMode)`: `Ordered` (exact + ORDER), `AnyOrder` (exact), `Contains` (no EXTRA rows,
others allowed); `Absent(actual, forbidden, noun)` for "not emitted" (a `Type`, or a partial = only
matching ones). **Exact pairs are a maximum matching (Kuhn), not greedy** — a broad expectation
written first must not steal the event a narrow one needs, and an ordered comparison seeds that
matching with the longest pairing that keeps the specification's order (#476), so ORDER is reported
only when no pairing avoids it. **Pass 2 — the same type with different values, a FAIL rather than a
MISSING beside an EXTRA — only pairs items that HAVE members to disagree about**: pairing two scalars
says "expected rush, was vip" about two items with nothing to do with each other. A matched partial
row shows only the specified members of the actual. `PropertyCells.Verify(subject, IPartialObject)`
gives a one-row grid of just the specified columns, and a collection member's cell carries every
difference *inside* it (`PartialMatching.Owns`) — keyed on the member path alone, a nested difference
reached no cell and a failed comparison rendered green.

### A collection member is a set, a dictionary member is keyed (bobcat#456)

**`ObjectComparison.Compare` is recursive**, not flatten-both-graphs-and-match-leaf-paths, and when it
meets a collection it delegates the matching to `ObjectSetVerification.Pair` — extracted from that
type's own grid so there is **one matching and two renderings**. Before this, a collection member was
compared position by position, so a reordered one reported every element as wrong: the exact failure
`ObjectSetVerification` exists to avoid, unreachable from inside an object. `Leaves` stays as the
flattener for rendering one graph; the comparison no longer uses it.

- **Unordered by default; strict ordering is opt in, case by case** (Jeremy, 2026-10-12). A bare
  collection member is `AnyOrder`. Most collection members have an order the system happens to produce
  rather than one the specification is about. `Expect.Exactly(…)` claims the order at the member level,
  `Expect.Value(x).InOrder(e => e.Lines)` for one member of a whole object.
- **`Expect`** carries the vocabulary beside `Expect.Value`: `Exactly` / `InAnyOrder` / `Containing` /
  `NoneOf` / `NoItems`, plus **`Rows(table, mode)`** and **`NoRowsLike(table)`** for a child collection
  written as a grid. The table forms are **named apart deliberately**: `StepTable` converts implicitly
  from a string, so an `Expect.Containing(StepTable)` overload beats `params object[]` for a single
  string argument and `Expect.Containing("vip")` over a collection of strings silently became a table
  with no rows — an expectation that asserts nothing and passes. A test caught it.
- An item is **any expectation** — a whole object, a `Specified<T>`, an `Expect.Value`, a bare `Type`.
  A null element becomes `NullItem` so a set verification (which reads an item's runtime type) can pair
  it. `ExpectedItems.ItemsFor(elementType)` materialises a table form; the element type comes from the
  member via `With<TValue>`, falling back to the actual collection's at compare time.
- **A nested disagreement names the item that disagreed** — `Lines[1].Quantity`, at the position the
  actual item is at — and an expectation nothing satisfied names the **member**, with no index, because
  it was not found anywhere. `ValueDifference` and `ObjectComparison.Leaf` carry a **trailing optional
  `Note`** (MISSING / EXTRA / out of order / present-when-forbidden / wrong type), the same move as
  `StepCell`'s trailing comparison word. `ValueDifferences` renders a list of them in one place, capped
  at **ten** with "…and N more" — a reordered fifty-element collection is a hundred disagreements — and
  the cap is at the rendering, so a programmatic caller still gets every one.
- **A dictionary member is keyed** (`Headers[accept]`), so two dictionaries holding the same entries
  agree whatever order they enumerate in and a key one side lacks reads as that key. Only a real
  dictionary: a `List<KeyValuePair<,>>` is ordered and may hold one key twice, so reading it by key
  would lose its order and silently drop a duplicate's leaves.
- **A member whose runtime type is not the expected one is one difference** naming both types, and the
  walk stops there — it was a cascade of leaves the actual has and the expectation does not, with the
  type claim never stated. The top level already requires the exact type, so the silent one at depth
  was the odd case out. Checked **after** the collection arms, where a `List<T>` and a `T[]` are the
  same claim. Naming members is unaffected: a dotted path asks about a member, not about which type
  carries it.
- **`ComparisonRules`** carries ignored paths, per-path `Tolerance` and the claimed-order paths, so the
  comparison's parameter does not grow a sibling per refinement; `IExpectedValue.Rules` is a defaulted
  member over `IgnoredPaths`. `Expect.Value(x).Approximately(m, 0.01m)` / `.Within(m, 2.Seconds())`
  render the `±` note a table cell already shows for `CheckOptions.Tolerance`. Slack on a member that
  cannot have any is a `SpecCriticalException` naming it — a specification asking for slack on a value
  that has none is a spec defect, and comparing exactly instead would hide it. **A rule names a path
  from the object compared and does not reach inside a collection's elements** (an element has no path
  a specification writes); an element that needs its own slack is its own `Expect.Value`.
- **The ancestor set is `[ThreadStatic]`, owned by the outermost call.** A collection's items are
  compared through `PartialMatching`, which re-enters `ObjectComparison`, so a set each call owned
  privately would be a fresh one per element and a cycle **through a collection** would not terminate.
  `MaxDepth = 8` stays as the backstop.
- **`ObjectComparison.Format` overrides a type's own `ToString` only where it has nothing of its own
  to say.** A collection's gives its type name where a reader wants its items, and a record's
  generated one walks the graph and recurses forever on a cycle (`PrintMembers` gives it away) — but a
  hand-written `ToString` is how the type wants to be read, which the first cut broke for the tag list.
- **Shouldly's `ShouldBe` over a collection stays ordered** (`MemberChecks.Read`), with
  `ignoreOrder: true` as the unordered form: `ShouldBe` is Shouldly's word and its own meaning over an
  `IEnumerable` is ordered. What Bobcat adds is member by member. `ShouldBe(x, tolerance)` still falls
  through to a `MemberCheck`. A collection expectation **cannot be built**, the same refusal a member
  check already gets.
- User docs: `docs/expected-objects.md`, which is also #452's home for the member-check C# 14
  requirement. Worked corpus: `Bobcat.Projected.Acceptance.Tests/ExpectedObjectTests`, asserting on the
  **grid** rather than the difference list, because #456 was about what a reader could tell.

**`RecordBuilding.Build`** (`src/Bobcat/CritterStack/`, public) is a thin call into this engine, and
every build is partial (#419). It fills through the process-wide `PartialObjects.UnspecifiedValues`
policy (#421), so an unspecified `Guid` is a fresh id rather than `Guid.Empty`; `DefaultValues`
(`default(T)`, the #241 rule) stays for a suite that wants the old fill. A blank cell is not
specified; a column matching nothing on the type still fails by name.

### BOBCAT032: table columns checked at compile time (`TableColumns`, `TableCallChecks`, bobcat#415/#420)

A **constant** table (literal, raw string, or a constant interpolated string such as
`$"| {nameof(Order.Total)} |"` — evaluated with `SemanticModel.GetConstantValue`) whose columns do
not name members of the type it is about is a build **error**. Recognized by call shape, not by
attribute, so a downstream wrapper (Wolverine's `Verify(state, table)`) is covered for free:

| Call shape | Type checked | Rule (`TableColumns.ColumnRule`) |
|---|---|---|
| `(object subject, StepTable expected)` — `PropertyCells.Verify`, any such pair | subject's static type | `Property`: properties and fields by `[Header]` title (which REPLACES the name), dotted |
| `(IEnumerable actual, StepTable expected)` — `SetVerificationComparer.Verify`, any such pair | element type (scalar elements skipped) | `Set`: same titles, top level only; constant `keyColumns` checked too |
| `PartialObjects.FromTable(typeof(T), table)` | `T` | `Partial`: property/field by name or title, or a public constructor parameter, dotted |
| `Specified<T>.With("path", value)` | `T` | `Partial` |
| `Expect.Rows(table)` / `Expect.NoRowsLike(table)` (bobcat#456) | the **member's** element type, from the enclosing `With<TValue>`'s inferred `TValue` | `Partial`; scalar elements skipped |

Vertical `| field | value |` tables check the first-column values. Indexers are refused, depth is 8.
`Expect.Rows` is the one shape whose type is **not on the call** — the whole point of the table form is
that the author never repeats the element type — so it reads the enclosing `With`; anywhere else it is
skipped, the same degradation as a non-constant table.

**Skipped (left to the run-time `invalid` cell):** a non-constant table; a subject typed `object`,
an interface, abstract, or a type parameter; and — for the two RUNTIME-type readers (property
check, set) — an **open class**: one that is not a struct, sealed, or a record and that some type
in the compilation or its non-framework references derives from (#415's option 1, chosen pending
the user's call), because a subclass may legitimately carry the column. The same rule applies to
each nested segment of a dotted path. A partial object builds exactly `T`, so it is always checked.

**Rendering (#420):** a projected (Shouldly) assertion's sentence shows `nameof(Foo.Bar)` and constant
interpolations by value (`"Bar"`), and a `Specify<T>().With(x => x.Bar, v)[.Build()]` argument as
`T(Bar: v)` (`ArgumentText`). Those sentences are the only place the generator renders C# argument
source; `[BobcatStep]` placeholders and marker comments already show run-time values/prose.
