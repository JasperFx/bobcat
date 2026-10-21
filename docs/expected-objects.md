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

Every example on this page is run end to end in
[`src/Bobcat.Xunit.Samples`](https://github.com/JasperFx/bobcat/tree/main/src/Bobcat.Xunit.Samples),
once holding and once disagreeing, and every output block is what that run printed. The order under
test is an `OrderGrammar`'s `TheOrder`: `O-1`, shipping to Austin, lines `ABC` ×2 and `DEF` ×1, headers
`accept` and `auth`, total `30.004`, placed at 09:00:01 on 2026-10-20:

<<< ../src/Bobcat.Xunit.Samples/Application/Orders.cs#placed-order{cs}

The two steps the examples call are what every `Then…` in the Critter Stack vocabulary does
underneath, over Bobcat's own engines: a partial object through `PropertyCells`, a whole or relaxed
object through `ObjectSetVerification`:

<<< ../src/Bobcat.Xunit.Samples/Grammars/OrderGrammar.cs#order-grammar{cs}

## A partial object names what the specification is about

`Specify<T>()` builds one in code. A member is named by an expression, so renaming the property
renames the specification with it -- the long-standing weakness of table-driven tools:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#partial-holds{cs}

```
  a partial object names only what it is about OK
  ──────────────────────────────────────────────────────
    ✓ Then  the order matches
╭───┬─────────────┬────────┬─────────╮
│ # │ ShipTo.City │ Total  │ Verdict │
├───┼─────────────┼────────┼─────────┤
│ 1 │ Austin      │ 30.004 │   OK    │
╰───┴─────────────┴────────┴─────────╯
```

Everything else about the order is unspecified: not asserted, and not shown in the report either, so
the member table *is* the claim. A nested member is a dotted path, and a wrong one is one failed cell
naming both values:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#partial-disagrees{cs}

```
  a partial object with a wrong nested member FAILED
  ─────────────────────────────────────────────────────
    ✗ Then  the order matches
╭───┬─────────────────────────────────┬────────┬─────────╮
│ # │ ShipTo.City                     │ Total  │ Verdict │
├───┼─────────────────────────────────┼────────┼─────────┤
│ 1 │ expected 'Dallas', got 'Austin' │ 30.004 │  FAIL   │
╰───┴─────────────────────────────────┴────────┴─────────╯
```

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

<<< ../src/Bobcat.Tests/Partial/SpecifyFromATableTests.cs#table-matches{cs}

### Member checks, and the C# version they need

For a claim that is not plain equality, write the member as an assertion. Any assertion library
works: the assertion runs against the member's actual value, and an exception is the disagreement.
The report shows each one in the same member table, as the assertion reads:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#member-checks-hold{cs}

```
  a member check is a claim that is not equality OK
  ────────────────────────────────────────────────────────
    ✓ Then  the order matches
╭───┬───────────────────────────┬─────────────────────┬─────────╮
│ # │ Total                     │ Lines               │ Verdict │
├───┼───────────────────────────┼─────────────────────┼─────────┤
│ 1 │ should be greater than 10 │ should not be empty │   OK    │
╰───┴───────────────────────────┴─────────────────────┴─────────╯
```

A failed check keeps the claim as written and shows the value that broke it:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#member-checks-disagree{cs}

```
  a member check that disagrees reports the assertions own message FAILED
  ────────────────────────────────────────────────────────────
    ✗ Then  the order matches (9ms)
╭───┬──────────────────────────────────────────┬─────────────────────┬─────────╮
│ # │ Total                                    │ Lines               │ Verdict │
├───┼──────────────────────────────────────────┼─────────────────────┼─────────┤
│ 1 │ expected 'should be greater than 100',   │ should not be empty │  FAIL   │
│   │ got '30.004' (30.004m)                   │                     │         │
╰───┴──────────────────────────────────────────┴─────────────────────┴─────────╯
```

A plain `ShouldBe` is the same as `.With(…)`. `.Check(…)` adds the same to a `.With(…)` chain. **A
check only verifies**, so an object carrying one cannot be built as a command or an arranged event.

**A check is one assertion on one member path** -- `x => x.Address.City.ShouldBe("Austin")`. Chained
Shouldly reads naturally and cannot be read as one member, so it is a build error, **BOBCAT044**, at
the lambda:

```csharp
// BOBCAT044: asserts on 'x.Attendees.ShouldHaveSingleItem().MemberId', which is not a member
Specify<Meeting>(x => x.Attendees.ShouldHaveSingleItem().MemberId.ShouldBe(theMemberId));

// Say what the collection holds instead
Specify<Meeting>().With(x => x.Attendees, Expect.Exactly(Specify<Attendee>().With(a => a.MemberId, theMemberId)));
```

The same goes for an assertion on the object itself (`x => x.ShouldNotBeNull()`) and for one comparing
two of its members (`x => x.Total.ShouldBe(x.Subtotal)`). Without the generator, the run refuses each
one with an `ArgumentException` saying the same thing.

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

## A whole object is judged member by member

Hand a step the object itself and every member is compared, structurally, with each disagreement
named by its path, a collection item by its position:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#whole-object-disagrees{cs}

```
  a whole object is judged member by member FAILED
  ───────────────────────────────────────────────────
    ✗ Then  the order is
╭───┬─────────────┬──────────────────────────────────────────────────┬─────────╮
│ # │ order       │ values                                           │ Verdict │
├───┼─────────────┼──────────────────────────────────────────────────┼─────────┤
│ 1 │ PlacedOrder │ expected 'ShipTo.City: Dallas,                   │  FAIL   │
│   │             │ Lines[1].Quantity: 9', got 'ShipTo.City: Austin, │         │
│   │             │ Lines[1].Quantity: 1'                            │         │
╰───┴─────────────┴──────────────────────────────────────────────────┴─────────╯
```

Two disagreements out of nine leaves, and only those two are reported.

## `Expect.Value` relaxes a whole object

The inverse of a partial object: it judges *everything except* what it names. Four refinements, each
naming one member:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#expect-value-holds{cs}

```
  an expected value relaxes the members it names OK
  ────────────────────────────────────────────────────────
    ✓ Then  the order is (4ms)
╭───┬─────────────┬──────────────────────────────────────────────────┬─────────╮
│ # │ order       │ values                                           │ Verdict │
├───┼─────────────┼──────────────────────────────────────────────────┼─────────┤
│ 1 │ PlacedOrder │ Id: "O-1", ShipTo: ShippingAddress(City:         │   OK    │
│   │             │ "Austin", Country: "US"), Lines: [OrderLine(Sku: │         │
│   │             │ "ABC", Quantity: 2), OrderLine(Sku: "DEF",       │         │
│   │             │ Quantity: 1)], Headers: [KeyValuePair<string,    │         │
│   │             │ string>(Key: "accept", Value: "json"),           │         │
│   │             │ KeyValuePair<string, string>(Key: "auth", Value: │         │
│   │             │ "token")], Total: 30.004, PlacedAt:              │         │
│   │             │ 2026-10-20T09:00:01.0000000+00:00                │         │
╰───┴─────────────┴──────────────────────────────────────────────────┴─────────╯
```

That expectation says `O-2`, `30` and 09:00:00, and holds against `O-1`, `30.004` and 09:00:01. An
**ignored** member is still rendered, as an unjudged value: the minted id is exactly what a reader
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
accident reads as every element being wrong the first time anything reorders. So the lines written
backwards still hold:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#collection-reordered-holds{cs}

```
  a reordered collection member is not a disagreement OK
  ────────────────────────────────────────────────────────────
    ✓ Then  the order matches
╭───┬────────────────────────────────────────────────────────────────┬─────────╮
│ # │ Lines                                                          │ Verdict │
├───┼────────────────────────────────────────────────────────────────┼─────────┤
│ 1 │ [OrderLine(Sku: "ABC", Quantity: 2), OrderLine(Sku: "DEF",     │   OK    │
│   │ Quantity: 1)]                                                  │         │
╰───┴────────────────────────────────────────────────────────────────┴─────────╯
```

until the specification says the order is the claim:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#collection-exactly-disagrees{cs}

```
  the order is the claim when the specification says so FAILED
  ────────────────────────────────────────────────────────────
    ✗ Then  the order matches (7ms)
╭───┬────────────────────────────────────────────────────────────────┬─────────╮
│ # │ Lines                                                          │ Verdict │
├───┼────────────────────────────────────────────────────────────────┼─────────┤
│ 1 │ expected '[0]: OrderLine(Sku: "ABC", Quantity: 2) (Out of      │  FAIL   │
│   │ order: OrderLine was appended at position 1, before one the    │         │
│   │ specification writes ahead of it (position 2))', got '[0]:     │         │
│   │ OrderLine(Sku: "ABC", Quantity: 2) (Out of order: OrderLine    │         │
│   │ was appended at position 1, before one the specification       │         │
│   │ writes ahead of it (position 2))'                              │         │
╰───┴────────────────────────────────────────────────────────────────┴─────────╯
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
brings its own `Expect.Value(…).Approximately(…)`. A collection of plain values works the same way:
`Expect.Containing("vip")` over a `string[]`.

### The three ways a collection disagrees

The right item with a wrong member is **that member**, at the position the reader will find the
item in:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#collection-wrong-member{cs}

```
  the right item with a wrong member is that member FAILED
  ───────────────────────────────────────────────────────────
    ✗ Then  the order matches
╭───┬───────────────────────────────────────────────────┬─────────╮
│ # │ Lines                                             │ Verdict │
├───┼───────────────────────────────────────────────────┼─────────┤
│ 1 │ expected '[1].Quantity: 9', got '[1].Quantity: 1' │  FAIL   │
╰───┴───────────────────────────────────────────────────┴─────────╯
```

An item nobody expected is **EXTRA**, and an expectation nothing satisfied is **MISSING**, reported
with no index because it was not found anywhere:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#collection-extra-and-missing{cs}

```
  an item nobody expected is extra FAILED
  ──────────────────────────────────────────
    ✗ Then  the order matches
╭───┬────────────────────────────────────────────────────────────────┬─────────╮
│ # │ Lines                                                          │ Verdict │
├───┼────────────────────────────────────────────────────────────────┼─────────┤
│ 1 │ expected '[1]: NULL (EXTRA: nothing expected a OrderLine       │  FAIL   │
│   │ here)', got '[1]: OrderLine(Sku: "DEF", Quantity: 1) (EXTRA:   │         │
│   │ nothing expected a OrderLine here)'                            │         │
╰───┴────────────────────────────────────────────────────────────────┴─────────╯

  an expectation nothing satisfied is missing FAILED
  ─────────────────────────────────────────────────────
    ✗ Then  the order matches
╭───┬────────────────────────────────────────────────────────────────┬─────────╮
│ # │ Lines                                                          │ Verdict │
├───┼────────────────────────────────────────────────────────────────┼─────────┤
│ 1 │ expected 'OrderLine(Sku: "GHI") (MISSING: no OrderLine like    │  FAIL   │
│   │ this)', got '[OrderLine(Sku: "ABC", Quantity: 2),              │         │
│   │ OrderLine(Sku: "DEF", Quantity: 1)]'                           │         │
╰───┴────────────────────────────────────────────────────────────────┴─────────╯
```

An expectation with no exact match is paired with an unmatched item of the same type first, which is
why `Quantity: 9` above is one failed member and not a MISSING beside an EXTRA, and why MISSING and
EXTRA show up mostly when the counts differ. Ten disagreements are shown, then `…and N more`: a
reordered fifty-element collection is a hundred disagreements, and the cap is at the rendering, so
code reading the differences still gets every one.

### A child collection as a table

For a collection whose elements read better as a grid than as a chain, `Expect.Rows` takes a table and
reads each row as one element, exactly as `Specify<T>(table)` reads one object. The element type comes
from the member, so you never repeat it:

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#collection-as-table{cs}

```
  a child collection may be written as a table OK
  ──────────────────────────────────────────────────────
    ✓ Then  the order matches (2ms)
╭───┬────────────────────────────────────────────────────────────────┬─────────╮
│ # │ Lines                                                          │ Verdict │
├───┼────────────────────────────────────────────────────────────────┼─────────┤
│ 1 │ [OrderLine(Sku: "ABC", Quantity: 2), OrderLine(Sku: "DEF",     │   OK    │
│   │ Quantity: 1)]                                                  │         │
╰───┴────────────────────────────────────────────────────────────────┴─────────╯
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

<<< ../src/Bobcat.Xunit.Samples/Specs/ExpectedObjectSpecs.cs#dictionary-keyed{cs}

```
  a dictionary member is compared by key FAILED
  ────────────────────────────────────────────────
    ✗ Then  the order is (1ms)
╭───┬─────────────┬──────────────────────────────────────────────────┬─────────╮
│ # │ order       │ values                                           │ Verdict │
├───┼─────────────┼──────────────────────────────────────────────────┼─────────┤
│ 1 │ PlacedOrder │ expected 'Headers[trace]: abc (MISSING: no entry │  FAIL   │
│   │             │ under 'trace'), Headers[accept]: NULL (EXTRA: no │         │
│   │             │ entry expected under 'accept')', got             │         │
│   │             │ 'Headers[trace]: NULL (MISSING: no entry under   │         │
│   │             │ 'trace'), Headers[accept]: json (EXTRA: no entry │         │
│   │             │ expected under 'accept')'                        │         │
╰───┴─────────────┴──────────────────────────────────────────────────┴─────────╯
```

A `List<KeyValuePair<,>>` is deliberately **not** read this way: it is an ordered list that may hold
one key twice, and reading it by key would lose its order and silently drop a duplicate's members.

::: warning Through a partial object, a dictionary is still one value
The keyed comparison runs when the dictionary is reached through a whole object or an
`Expect.Value`, as above. A partial object naming the member directly,
`Specify<PlacedOrder>().With(x => x.Headers, …)`, compares the dictionary as one value today and
reports one cell with both sides written out
([bobcat#761](https://github.com/JasperFx/bobcat/issues/761)).
:::

## Values with their own meaning

| | |
|---|---|
| **Strong-typed ids** | a Guid-based id reads as the name its Guid was declared under (`#theScreeningId`); any other as `ScreeningId(SCR-104)`, however the type is built -- `record struct`, Vogen, StronglyTypedId |
| **Guids** | named after the property they were first seen under, minus a trailing `Id` (a bare `Id` takes its type's name, a second value `#Order2`), and read with a leading `#` -- `OrderStarted(OrderId: #Order)`, `there is no Product #Product` -- so a name is never mistaken for a type or a literal. The scenario's report lists each bare name beside its value |
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
- [Shouldly Assertions as Steps](shouldly.md) -- Shouldly inside a specification, including member checks
- [Writing Specifications](marker-steps.md) -- the three ways a step arrives, and BOBCAT032, which
  catches a stale member path at build time
- [Testing with Bobcat](https://wolverinefx.net/guide/testing#with-wolverinefx-bobcat) (Wolverine docs) -- the Critter Stack steps
  that take an expected object
