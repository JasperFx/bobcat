# Expected Objects

Wherever a Bobcat specification says what an object should be -- an event a command appended, a read
model, a message, an HTTP response body, anything you hand to `ThenMatches` -- it says it in one of
three forms. They are accepted interchangeably, by every step that takes an expected object.

| | judges | |
|---|---|---|
| **A whole object** | every member, structurally | `ThenEvents(new ShipmentConfirmed(theOrder, "UPS", "1Z999"))` |
| **`Expect.Value(…)`** | every member except the ones it relaxes | `Expect.Value(confirmed).Ignoring(x => x.ShippedAt)` |
| **A partial object** | **only** the members it names | `Specify<ShipmentConfirmed>().With(x => x.Carrier, "UPS")` |

Nothing here ever compares through `Equals`. A record's generated equality is silently wrong for a
collection member -- it compares the collection by reference -- so the comparison walks the graph
instead, member by member, and reports each disagreement as its own cell.

## A partial object names what the specification is about

`Specify<T>()` builds one in code. A member is named by an expression, so renaming the property
renames the specification with it -- the long-standing weakness of table-driven tools:

```csharp
ThenEvents(Specify<ShipmentConfirmed>()
    .With(x => x.Carrier, "UPS")
    .With(x => x.TrackingNumber, "1Z999"));
```

Everything else about the event is unspecified: not asserted, and not shown in the report either, so
the member table is the claim and nothing else.

A partial object can also be **built**, which is what makes it the way to write a command or an
arranged event as well as an expectation. Members it does not name are filled by the fill policy
(`PredictableValues` by default -- a fresh `Guid`, the empty string, an empty collection, the Bobcat
clock's now).

| | |
|---|---|
| `.With(x => x.Address.City, "Austin")` | a nested member, as a dotted path |
| `.With(x => x.Address, Specify<Address>().With(a => a.City, "Austin"))` | the same claim, written as a nested partial |
| `.With(nameof(T.Prop), value)` | a string path, which `nameof` keeps honest |
| `.With(x => x.Address, null)` | a member that is null; a bare `null` needs no cast |
| `Specify<T>(table)` | a table: `Property \| Value` rows, or the members as headers over one row |
| `.Check(x => x.Age.ShouldBeGreaterThan(3))` | an assertion on a member, for a claim that is not equality |

An object with more than about three specified members reads better as a table than as a chain that
runs off the screen. Cells mean what they mean in [every other Bobcat
table](tutorials/data-intensive-specifications.md#what-a-written-cell-may-say) -- `NULL`, `EMPTY`,
relative times -- and a **blank cell is not specified**, which is how one table can describe objects
of several types under the union of their members:

```csharp
await WhenReceived(Specify<BookVisit>($$"""
    | Property | Value        |
    | VisitId  | {{theVisit}} |
    | Vet      | Dr. Hollis   |
    | Room     | 3            |
    | Notes    | EMPTY        |
    """));
```

### Member checks, and the C# version they need

For a claim that is not plain equality, write the member as an assertion. Any assertion library
works: the assertion runs against the member's actual value, and an exception is the disagreement.

```csharp
await ThenReadModel<ApplicationsQueue>(theQueue, Specify<ApplicationsQueue>(
    x => x.Pending.ShouldBe(1),
    x => x.Oldest.ShouldBeLessThan(DateTimeOffset.UtcNow)));
```

The report shows each one in the same member table, as the assertion reads (`should be less than …`),
with the assertion's own message when it fails. A plain `ShouldBe` is the same as `.With(…)`.
`.Check(…)` adds the same to a `.With(…)` chain. **A check only verifies**, so an object carrying one
cannot be built as a command or an arranged event.

::: warning Member checks need C# 14 to read naturally
A member check is an expression tree, which is how Bobcat reads the member path and the expected
value out of it -- and **before C# 14 an expression tree cannot leave out an optional argument**,
which every Shouldly assertion has (`customMessage`). The error is
**CS0854**, "An expression tree may not contain a call or invocation that uses optional arguments".

| Language version | What compiles |
|---|---|
| C# 14 (net10.0's default) | `x => x.Age.ShouldBe(52)` |
| C# 12 or 13 (net9.0, or a pinned `LangVersion`) | `x => x.Age.ShouldBe(52, null)` -- every optional argument written out, positionally |

On C# 12 or 13, prefer the `.With(…)` chain or a table unless the claim genuinely is not equality.
:::

## `Expect.Value` relaxes a whole object

The inverse of a partial object: it judges *everything except* what it names. Four refinements, each
naming one member:

```csharp
ThenEvents(Expect.Value(confirmed)
    .Ignoring(x => x.AssignmentId)                         // shown, never judged
    .Approximately(x => x.Total, 0.01m)                    // within an absolute amount
    .Within(x => x.ConfirmedAt, TimeSpan.FromSeconds(2))   // within a window of time
    .InOrder(x => x.Lines));                               // this collection's order is the claim
```

An **ignored** member is still rendered, as an unjudged value: the minted id is exactly what a reader
wants to see when the spec goes red. An ignored path covers everything beneath it.

A **window** is usually the real claim where `Ignoring` was the only option: the timestamp had to be
about now, not "whatever it was". The `±` note it renders in the cell is the one a table cell already
shows for an approximate column, so a typed comparison and a cell comparison say the same thing about
the same slack. Asking for slack on a member that cannot have any -- a window over a string, an
amount over a date -- is a `SpecCriticalException` naming the member, because that is a mistake in
the specification rather than a disagreement.

## A collection member is a set

A collection member is matched **exactly these items, in any order**: an item nobody expected is
EXTRA, an expectation nothing satisfied is MISSING, and the right item with a wrong member is one
failed member rather than a missing row beside an extra one. The same reading a top-level
`ThenEventsInAnyOrder` gets, because it is the same matching.

**Unordered is the default and strict ordering is opt in**, because most collection members have an
order the system happens to produce rather than one the specification is about -- and asserting it by
accident reads as every element being wrong the first time anything reorders.

```csharp
ThenMatches(theOrder, Specify<Order>()
    .With(x => x.Lines, Expect.Exactly(                     // ... in THIS order
        Specify<OrderLine>().With(l => l.Sku, "ABC"),
        Specify<OrderLine>().With(l => l.Sku, "DEF")))
    .With(x => x.Tags, Expect.Containing("vip"))            // among whatever else
    .With(x => x.Notes, Expect.NoItems()));                 // empty
```

| | |
|---|---|
| a bare collection | exactly these items, in any order |
| `Expect.Exactly(…)` | exactly these, **in this order** |
| `Expect.InAnyOrder(…)` | exactly these, in any order -- the default, written out |
| `Expect.Containing(…)` | these, among whatever else; nothing is unexpected |
| `Expect.NoneOf(…)` | none of these: a `Type` for "nothing of that type at all", a partial object for "none that agrees on the members it names" |
| `Expect.NoItems()` | empty |
| `Expect.Rows(table)` / `Expect.Rows(table, SetMode.Ordered)` | the elements as a table, one row per element |
| `Expect.NoRowsLike(table)` | `NoneOf`, as a table |

**An element is an expectation in its own right** -- a whole object, a `Specify<T>()`, an
`Expect.Value(…)`, or a bare `Type` meaning "an item of this type, values unexamined". So a
collection of partial objects judges only what each one names, and an element needing its own slack
brings its own:

```csharp
.With(x => x.Lines, Expect.InAnyOrder(
    Expect.Value(new OrderLine("ABC", 2)),
    Expect.Value(new OrderLine("DEF", 1)).Approximately(x => x.Price, 0.01m)))
```

A collection of plain values works the same way: `Expect.Containing("vip")` over a `string[]`.

### A child collection as a table

For a collection whose elements read better as a grid than as a chain, `Expect.Rows` takes a table and
reads each row as one element, exactly as `Specify<T>(table)` reads one object. The element type comes
from the member, so you never repeat it:

```csharp
ThenMatches(theOrder, Specify<Order>().With(x => x.Lines, Expect.Rows("""
    | Sku | Quantity |
    | ABC | 2        |
    | DEF | 1        |
    """)));
```

The order is an argument (`Expect.Rows(table, SetMode.Ordered)`) rather than a separate method name,
and that is deliberate: a `StepTable` converts implicitly from a string, so an
`Expect.Containing(table)` overload would win against `Expect.Containing(params object[])` for a
single string argument -- and `Expect.Containing("vip")` over a collection of strings would silently
have meant "a table that happens to have no rows", which asserts nothing and passes.

### Shouldly's `ShouldBe` over a collection stays ordered

```csharp
Specify<Order>(x => x.Lines.ShouldBe(theExpectedLines))                     // ordered
Specify<Order>(x => x.Lines.ShouldBe(theExpectedLines, ignoreOrder: true))  // unordered
```

Not an inconsistency with the unordered default: `ShouldBe` is Shouldly's word, Shouldly's own
`ShouldBe` over an `IEnumerable` *is* ordered, and `ignoreOrder` is Shouldly's own opt-out, so a
specification that borrowed the spelling gets the meaning. What Bobcat adds is **member by member** --
Shouldly compares elements with `Equals`, which is reference equality for a class.

## A dictionary member is keyed

A dictionary compares by key, not by position, so two dictionaries holding the same entries agree
whatever order they enumerate in. A key one side lacks reads as that one key:

```
Headers[auth]   expected token   was NULL    MISSING: no entry under 'auth'
```

A `List<KeyValuePair<,>>` is deliberately **not** read this way: it is an ordered list that may hold
one key twice, and reading it by key would lose its order and silently drop a duplicate's members.

## What a failure reads like

Indexes appear in **what a comparison reports** and never in what a specification writes. A whole
object gets one cell per leaf:

```
Then the ShipmentConfirmed matches
  ShipTo.City       expected Dallas   was Austin
  Lines[1].Quantity expected 9        was 1
  Lines             OrderLine(Sku: "GHI")   MISSING: no OrderLine like this
  Lines[2]                                  EXTRA: nothing expected a OrderLine here
```

A **partial object** gets one cell per member it named -- that is the claim, so it is also the grid --
and a collection member's cell carries everything inside it, the member's own name trimmed off:

```
Then the order matches
  | ShipTo.City | Lines              |
  | Dallas      | [1].Quantity: 9    |   FAILED
  | Austin      | [1].Quantity: 1    |
```

- A **paired** element reports its own members, at the position the actual item is in -- that is where
  the reader will find it.
- An **unmatched** expectation reports the member itself, with no index, because it was not found
  anywhere and no position is the one it should have been at.
- **The right item with a wrong member is that member**, not a MISSING beside an EXTRA. An
  expectation with no exact match is paired with an unmatched item of the same type first -- "the
  right event with the wrong amount" is one failed member, which is also why MISSING and EXTRA show
  up mostly when the counts differ.
- Ten disagreements are shown, then `…and N more`. A reordered fifty-element collection is a hundred
  disagreements and a cell holding all of them is unreadable. The cap is at the rendering, so code
  reading the differences still gets every one.

## Values with their own meaning

| | |
|---|---|
| **Strong-typed ids** | a Guid-based id reads as the name its Guid was declared under (`theScreeningId`); any other as `ScreeningId(SCR-104)`, however the type is built -- `record struct`, Vogen, StronglyTypedId |
| **Guids** | named after the property they were first seen under, minus a trailing `Id`, and the scenario's report lists each name beside its value |
| **Enums** | compare by value or by name, so `Status.Open` agrees with `"Open"` |
| **Decimals** | scale is not the claim: `2.50m` agrees with `2.5m` |
| **F# options and single-case unions** | compare by what they hold, so `Some "UPS"` agrees with `"UPS"` and `OrderId g` with `g` |
| **A field-less stub** | the type is the whole of what it says, which is what an imported Event Model's records are |
| **Tags** | `Specify<SeatReserved>().Tagged(theScreeningId)` -- exactly those tags, in any order |

## Limits worth knowing about

- **A specification never indexes into a collection.** `Lines[0].Sku` is a question a set
  verification answers properly; a path a specification writes (`PropertyCells`, a partial object's
  member) is refused with a message rather than failing quietly.
- **A relaxation names a path from the object compared**, so it does not reach inside a collection's
  elements -- an element has no path a specification writes. An element that needs its own slack is its
  own expectation, as above.
- **Depth is capped at 8**, and a graph that refers back to itself says so once (`<cycle back to
  Node>`) rather than unrolling.
- **A member of the wrong runtime type is one difference**, naming both types, and the walk stops
  there. Naming members is unaffected: a dotted path asks about a member, not about which type carries
  it.

## See also

- [Data Intensive Specifications](tutorials/data-intensive-specifications.md) -- tables, set
  verification, decision tables, and what a written cell may say
- [Writing Specifications](marker-steps.md) -- the three ways a step arrives, and BOBCAT032, which
  catches a stale member path at build time
- [Bobcat with Marten, Polecat and Fisher](integrations/marten.md) and
  [Bobcat with Wolverine](integrations/wolverine.md) -- the steps that take an expected object
