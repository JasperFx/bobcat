# Structural comparison of complex objects — nested properties and collection members

Design candidate for [bobcat#456], raised in the DCB design conversation
([JasperFx/wolverine#4865], 10/9) as one of the two longer conversations to have first.

Everything in §1 was **measured** against `origin/main` (f82aef6) with a throwaway probe test over
`PartialMatching.Differences`, `ObjectComparison.Leaves`, `PropertyCells.Cells` and
`ObjectSetVerification.Verify`. The probe is not committed; the output is quoted verbatim.

---

## 1. What happens today

Subject for every row below:

```csharp
record Line(string Sku, int Quantity);
record Basket(string Id, List<Line> Lines);

var actual = new Basket("B1", [new Line("A", 1), new Line("B", 2)]);
```

`ObjectComparison` flattens a graph to leaves and matches them **by path string**, so a collection
is compared **position by position** and there is no way to say anything else.

### 1.1 A reordered collection reads as every element being wrong

```
PartialMatching.Differences(actual, new Basket("B1", [new Line("B", 2), new Line("A", 1)]))
   Lines[0].Sku: expected B, was A
   Lines[0].Quantity: expected 2, was 1
   Lines[1].Sku: expected A, was B
   Lines[1].Quantity: expected 1, was 2
```

Four differences, none of them saying *reordered*. This is exactly the failure
`ObjectSetVerification` was built to avoid — "comparing position by position reads one missing event
as every later event wrong" — and the same data at the **top** level gets the good reading today:

```
ObjectSetVerification.Verify(actual.Lines, [Specify<Line>().With(x => x.Sku, "B"),
                                            Specify<Line>().With(x => x.Sku, "A")], "line", Ordered)
   row1 out-of-order [failed]  Out of order: Line was appended at position 1,
                               before one the specification writes ahead of it (position 2)
```

**The set reading already exists and is good. It is simply not reachable from inside an object.**
That is the whole of this issue.

### 1.2 A longer or shorter collection reports a null leaf per member

```
one extra actual element      one missing actual element
   Lines.Count: 1 → 2            Lines[2].Sku: expected C, was NULL
   Lines[1].Sku: NULL → B        Lines[2].Quantity: expected 3, was NULL
   Lines[1].Quantity: NULL → 2   Lines.Count: expected 3, was 2
```

MISSING and EXTRA are the right words and are already in the vocabulary; a null leaf per member is
not.

### 1.3 A partial object's collection member reports the same text on both sides

```csharp
Specify<Basket>().With(x => x.Lines, new List<Line> { new("B", 2), new("A", 1) })
```
```
   Lines: expected System.Collections.Generic.List`1[Bobcat.Tests.Partial.Line],
            was System.Collections.Generic.List`1[Bobcat.Tests.Partial.Line]
```

**The worst output in the system**: a failure whose two sides are identical, for a genuinely
reordered list *and* for a list with one wrong quantity — the same line either way. The cause is
narrow: `agrees` compares the collection with `ObjectComparison.Compare` (leaf paths `[0].Sku`, …),
and then builds one `ValueDifference` whose `Expected`/`Actual` are the whole collections, which
`ObjectComparison.Format` renders through `object.ToString()`.

The machinery to render it properly is already there and already used elsewhere:

```
ScenarioValues.DescribeProperties(actual)
   Id: "B1", Lines: [Line(Sku: "A", Quantity: 1), Line(Sku: "B", Quantity: 2)]
ScenarioValues.Format(actual.Lines)
   System.Collections.Generic.List`1[Bobcat.Tests.Partial.Line]
```

### 1.4 A dictionary is compared by insertion order

```
ObjectComparison.Leaves(new WithDictionary(new() { ["accept"] = "json", ["auth"] = "token" }))
   Headers[0].Key = accept     Headers[0].Value = json
   Headers[1].Key = auth       Headers[1].Value = token
   Headers.Count = 2

the same two entries, inserted in the other order:
   Headers[0].Key: expected auth, was accept
   Headers[0].Value: expected token, was json
   Headers[1].Key: expected accept, was auth
   Headers[1].Value: expected json, was token
```

Four differences between two dictionaries that are equal. `KeyValuePair` is just an object with two
properties to the flattener, so a dictionary inherits positional comparison it has no business
having.

### 1.5 A cyclic graph does not hang the comparison — it blows the stack in the renderer

`MaxDepth = 8` stops `flatten`, but the leaf it emits **at** the cap is the object itself:

```
record Cyclic(string Name) { public Cyclic? Next { get; set; } }   // head.Next = head
   Name = a
   Next.Name = a
   … seven levels …
   Next.Next.Next.Next.Next.Next.Next.Next = <the Cyclic>    ← and formatting this recurses forever
```

Printing that leaf threw. `ObjectComparison.Format` ends in `value.ToString()`, and a record's
generated `ToString` walks its members. The depth cap protects the walk and not the rendering.

### 1.6 A collection in a table column is `invalid`, correctly

```
PropertyCells.Cells(actual, "| Id | Lines |\n| B1 | x |")
   Id    [success] expected=B1 actual=B1
   Lines [invalid] could not convert 'x' to List`1
```

No change wanted here: a table cell cannot hold a collection, and `invalid` says the specification
asked a question the column cannot express. Indexing (`Lines[0].Sku`) stays refused in a
specification-written path, per `PropertyCells`' existing note.

### 1.7 What already works and is not in question

- Nested **properties** — `Address.City` through `MemberPaths`, `PropertyCells.Resolve` and the
  flattener, with `[Header]` titling applied per segment (#411). A nested `Specify` flattens to a
  dotted path. None of this needs changing.
- Strong-typed ids (#433): a Guid-based id renders as its declared name, anything else as
  `TypeName(value)`.
- Enums: `LeafEquals` falls back to formatted text, so `Status.Open` agrees with `"Open"`.
- Decimal scale: `2.50m` agrees with `2.5m` through `Equals`.
- `Expect.Value(x).Ignoring(…)` — shown, not judged, and a path covers everything beneath it.
- Tags on an expectation (wolverine#4865), compared as an exact unordered set.

---

## 2. The proposal

One sentence: **a collection member's expectation is an expectation object rather than a collection,
and when the comparison meets one it delegates to `ObjectSetVerification`** — which already does
maximum matching, in-order seeding (#476), near-match FAIL rows, MISSING, EXTRA and ORDER.

### 2.1 `IExpectedItems`, and `Expect` as the one entry point

`Expect.Value` is already where an expectation that is not a plain object is built, so the
collection vocabulary joins it rather than starting a second static class:

```csharp
Expect.Exactly(params object[] items)      // SetMode.Ordered   — these, in this order
Expect.InAnyOrder(params object[] items)   // SetMode.AnyOrder  — exactly these, order irrelevant
Expect.Containing(params object[] items)   // SetMode.Contains  — these among whatever else
Expect.NoneOf(params object[] items)       // none of these are present
Expect.NoItems()                           // empty
```

A bare collection is `InAnyOrder` (call 1), so `Expect.Exactly` is the only one of these that says
something a bare list does not. Each also takes a `StepTable` in place of the items (call 2), one
row per element:

```csharp
.With(x => x.Lines, Expect.Exactly("""
    | Sku | Quantity |
    | ABC | 2        |
    | DEF | 1        |
    """))
```

An item is anything an expectation already is: a whole object, a `Specified<T>`, an
`Expect.Value(…)`, or a bare `Type` (— "an item of this type, values unexamined", which
`ObjectSetVerification` and `PartialMatching.ExpectedType` already read).

```csharp
Specify<Order>()
    .With(x => x.Lines, Expect.InAnyOrder(
        Specify<OrderLine>().With(l => l.Sku, "ABC"),
        Specify<OrderLine>().With(l => l.Sku, "DEF")))
    .With(x => x.Tags, Expect.Containing("vip"))
    .With(x => x.Lines, Expect.NoneOf(Specify<OrderLine>().With(l => l.Sku, "RECALLED")));
```

This answers #456 Q1 (ordered/unordered, contains/exactly) and the second half of Q1 — **yes, a
collection's elements can be partial objects**, because an item is an expectation and
`PartialMatching` already judges one. Scalar collections come free: `Expect.InAnyOrder("vip",
"rush")` over `string[] Tags` pairs two strings through the same path.

New `Specified<T>` overload, unambiguous because no member is typed as an expectation:

```csharp
public Specified<T> With<TValue>(Expression<Func<T, TValue>> member, IExpectedItems items)
```

### 2.2 Relaxing a whole-object expectation

`Expect.Value` is the form for "the whole event, except…", and today the only exception it can make
is `Ignoring`. It gains the rest of Q4 and a collection relaxation, each naming one member:

```csharp
Expect.Value(confirmed)
    .Ignoring(x => x.AssignmentId)                        // shown, not judged (exists)
    .Approximately(x => x.Total, 0.01m)                    // numeric tolerance
    .Within(x => x.ConfirmedAt, TimeSpan.FromSeconds(2))   // time tolerance
    .InAnyOrder(x => x.Lines);                             // judge Lines as a set, order irrelevant
```

Tolerance is carried per path and honoured by `LeafEquals`; the plumbing exists —
`CheckOptions.Tolerance` is the same figure `CellCheck` already uses for a table cell, so a typed
comparison and a cell comparison will disagree in the same words. For a partial object the existing
`.Check(x => x.Total.ShouldBe(100m, 0.01m))` is already the spelling and needs nothing.

### 2.3 What a nested set difference reports

`ValueDifference` stays the model and gains a **trailing optional** `Note`, the same move as
`StepCell`'s trailing comparison word — every existing construction compiles, and a renderer that
ignores it is exactly as correct as it was.

| verdict | `Path` | `Expected` | `Actual` | `Note` |
|---|---|---|---|---|
| element paired, leaves differ | `Lines[0].Quantity` | `2` | `1` | — |
| expected element unmatched | `Lines` | `OrderLine(Sku: "DEF")` | — | `MISSING` |
| actual element unmatched | `Lines[2]` | — | `OrderLine(Sku: "XYZ")` | `EXTRA` |
| paired but misplaced | `Lines[1]` | — | — | the existing out-of-order sentence |
| forbidden element present | `Lines[1]` | — | `OrderLine(Sku: "RECALLED")` | the existing present sentence |

The rule behind the first two rows, which is also `ObjectComparison`'s existing rule ("indexes
appear in what this reports, never in what a spec writes"): **a paired element reports its leaf
paths; an unmatched expected element reports the member, because it was not found anywhere.** The
sentences come from `ObjectSetVerification.Problems`, so nested and top-level failures are worded
identically.

### 2.4 Dictionaries by key (Q5, first half)

`flatten` special-cases `IDictionary` and `IReadOnlyDictionary<,>`: the leaf path is
`Headers[accept]`, the key formatted through `ScenarioValues.Format`. Matching by path then makes a
dictionary order-independent **for free**, a missing key reads `Headers[auth]: expected token, was
NULL`, and an unexpected key reads as its own path. No API, and §1.4 stops being wrong.

### 2.5 Polymorphic members (Q5, second half)

**Compare by the runtime type, and a type disagreement is one difference rather than a cascade.**
Where both sides of a nested branch are non-null non-leaves and the runtime types differ, report
`Payment: expected CardPayment, was CashPayment` and do not descend.

Today the type claim is made silently and reported as a pile of leaves the actual object has and
the expectation does not. This also makes depth consistent with the top level, where
`PartialMatching.Matches` already requires the exact type. It does not restrict naming members: a
specification that means "any payment, with these members" writes
`.With(x => x.Payment.Amount, …)` or a nested `Specify<Payment>()`, and a dotted path never checks a
type.

### 2.6 Depth and cycles (Q3)

- `ObjectComparison.Format` stops calling `object.ToString()` on a non-leaf and uses
  `ScenarioValues.Describe`, which is cycle-safe (it prints a type name at its own depth cap and
  never calls a user `ToString`). §1.5 and §1.3 both stop being wrong.
- `flatten` carries a reference-identity set for the current path, so a cycle is a leaf saying so
  rather than eight levels of noise.
- `MaxDepth = 8` stays as the backstop, and the leaf at the cap is `Describe` text — deterministic,
  the same on both sides, so a deep-but-equal graph still agrees.

### 2.7 Rendering a deep failure (Q6)

**One flat grid, paths as names** — `Lines[0].Quantity` as the path, which is what `PropertyCells`
and `ObjectSetVerification` already render and what the monitor wire already carries. The
alternative, a nested sub-grid per collection member, is a new rendering shape in four surfaces
(Spectre, plain text, the JSON report and the wire — and the wire has an external consumer in Stoat
with no assembly reference to warn it), so it is worth doing only once a real failure proves the
flat form unreadable.

What does need a cap, with precedent: **ten differences per comparison, then `…and N more`.** A
reordered 50-element list is 100 differences joined into one cell today. The cap goes where
differences are *rendered*, not where they are computed, so a programmatic caller still gets all of
them — the same split as `TableReport`'s 200-row cap, which exists because a suite once dumped a
698MB log into test output.

---

## 3. Calls settled (Jeremy, 2026-10-12)

1. **Unordered by default. Strict ordering is opt in, case by case.** A bare collection member — and
   a collection inside a whole-object comparison — means *exactly these items, in any order*;
   `Expect.Exactly(…)` is how a specification says order is part of the claim. This reverses §2's
   first draft, which kept ordered-and-exact on the grounds that a `List<T>` is ordered in C#. The
   answer to that: most collection members have an order the system happens to produce rather than
   one the specification is about, and §1.1 is what asserting it by accident costs.
2. **A child collection may be written as a table, inline** — the `VerifySet` shape, for the member
   whose rows read better as a grid than as a `.With(…)` chain. §2.1 carries it:
   `PartialObjects.FromTable(elementType, table)` already turns a table into one partial object per
   row, and the element type comes from the member's declared type, so an author never repeats it.
3. **Polymorphic nested member**: one difference at that path, do not descend (§2.5).
4. **Rendering**: one flat grid with paths as names, plus the cap (§2.7).
5. **The vocabulary lives on `Expect`** (§2.1). **And Shouldly's `ShouldBe` on a collection means an
   ordered, member-by-member comparison** — §2.2a.

### 2.2a `ShouldBe` on a collection

`Specify<T>(x => x.Lines.ShouldBe(lines))` and `.Check(x => x.Lines.ShouldBe(lines))` read
`ShouldBe` as **ordered**, compared member by member, with `ShouldBe(lines, ignoreOrder: true)` as
unordered. That is not an inconsistency with call 1: `ShouldBe` is Shouldly's word, Shouldly's own
`ShouldBe` over an `IEnumerable` is ordered, and `ignoreOrder` is Shouldly's own opt-out — so
honouring the library's meaning is the honest reading of a spec that borrowed its spelling. What
Bobcat adds is *member by member*: Shouldly compares elements with `Equals`, which is reference
equality for a class and is "silently wrong" for exactly the reason `ObjectComparison` exists.

Open, and deliberately left for later: a **bare** `order.Lines.ShouldBe(expected)` statement in a
spec body goes through the projected-assertion interceptor, which gathers Shouldly's own verdict
rather than replacing the comparison. Changing that reaches into the generator and
`ShouldlyDialect`, and is a bigger piece than this issue.

## 4. Order of work

Each row stands alone and lands on its own.

| | change | risk | |
|---|---|---|---|
| 1 | §2.6 — `Format` via `Describe`, cycle detection | none; fixes §1.3's identical-sides line and §1.5 outright | done, `28be13b` |
| 2 | §2.4 — dictionaries by key | behavioural: two dictionaries that disagreed now agree | done, `28be13b` |
| 3 | §2.1 — `IExpectedItems` + `Expect.*` + the `Specified<T>` overload + the inline table, delegating to `ObjectSetVerification`; **a bare collection becomes unordered** | behavioural, per call 1 | done, `a52b748` |
| 4 | §2.3 — `ValueDifference.Note` and the nested verdict rows | additive; 3 is not worth shipping without it | done, `a52b748` |
| 5 | §2.7 — the difference cap | rendering only | done, `a52b748` |
| 8 | §2.5 — the runtime-type check | behavioural | done, `a52b748` |
| 6 | §2.2a — `ShouldBe` on a collection is ordered | additive | done, `102b604` |
| 7 | §2.2 — `Approximately` / `Within` / `InOrder` on `Expect.Value` | additive | done, `ac19946` |
| 9 | docs + worked samples (#452's home too) | — | done, `94a2645` |
| 10 | the ai-skills entry | — | staged, `d550215` — see `ai-skills-456.md` |
| 11 | BOBCAT032 for `Expect.Rows(table)` | additive | done |

`.InAnyOrder(member)` from §2.2's first draft was **not built, and should not be**: call 1 made a bare
collection unordered, so it would have been a no-op. `.InOrder(member)` is the method that earns its
place now — `Expect.Exactly` for one member of a whole object.

### What building it taught

Four things the design did not foresee, each now pinned by a test:

- **`Expect.Containing("vip")` silently meant "a table".** `StepTable` converts implicitly from a
  string, so an overload taking one beats `params object[]` for a single string argument — and a
  table parsed from `"vip"` has no rows, which is an expectation that asserts nothing and passes.
  The table forms are therefore named apart: `Expect.Rows(table, mode)` and `Expect.NoRowsLike(table)`.
- **Near-match pairing must not pair scalars.** Pass 2 of the set matching exists to say "the right
  thing with a wrong member"; a scalar has no members, so pairing two strings says "expected rush,
  was vip" about two items with nothing to do with each other, where MISSING beside EXTRA is the
  truth. The condition is in `Pair`, so the top level gets it too.
- **The ancestor set has to span the whole comparison, not one call.** A collection's items are
  compared through `PartialMatching`, which re-enters `ObjectComparison` — so a set each call owned
  privately would be a fresh one per element, and a graph that cycles *through a collection* would
  not terminate. It is `[ThreadStatic]`, owned by the outermost call.
- **`Format` may not override a type's own `ToString` indiscriminately.** §2.6's first cut sent every
  non-leaf to `Describe`, which broke the tag list, whose whole job is its `ToString`. A **record's**
  generated one still does not count — it walks the members the graph walk already covers and
  recurses forever on a cycle — and `PrintMembers` is how it gives itself away.

One consequence of §2.3 that had to be fixed with it: `PropertyCells.Verify(subject, partial)` keyed
differences on the member path alone, so a nested difference (`Lines[1].Quantity`) reached no cell
and a failed comparison **rendered green** — and two items missing at one path threw on a duplicate
dictionary key. A member now owns every path inside it (`PartialMatching.Owns`).

Then, and Jeremy's own reading of this issue is that it is most of the value: **documentation and
worked samples** for the expected-object forms, which is also #452's homeless docs. Plus the
ai-skills entry (`critterstack-sdd-bobcat-authoring`), per this repo's rule that a capability ships
with the write-up that teaches a later agent to use it.

Follow-ups this opens, **not** in scope here:

- The bare-statement `ShouldBe` through the projected-assertion interceptor (§2.2a) — a statement in a
  spec body still gathers Shouldly's own verdict rather than Bobcat's comparison. Reaches into the
  generator and `ShouldlyDialect`.
- **Multi-case F# unions** are still unsupported by the comparison, unchanged by this work.

Done rather than deferred: **BOBCAT032 now covers `Expect.Rows(table)`**, which was listed here as a
follow-up. It is the one checked shape whose type is *not on the call* — the whole point of the table
form is that the author never repeats the element type — so `TableCallChecks` reads the enclosing
`With<TValue>`'s inferred member type, and skips where no member is in view.

[bobcat#456]: https://github.com/JasperFx/bobcat/issues/456
[JasperFx/wolverine#4865]: https://github.com/JasperFx/wolverine/issues/4865
