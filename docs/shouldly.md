# Shouldly Assertions as Steps

A Bobcat specification can be written entirely from step helpers and `SpecAssert`, and many are.
But most test suites already assert with [Shouldly](https://docs.shouldly.org/), and rewriting every
`ShouldBe` into a helper is the kind of migration nobody finishes. So Bobcat reads Shouldly: with one
project setting, an ordinary statement-level Shouldly call inside a specification becomes a step of
its own, with a sentence, a verdict, a duration and, where the comparison allows, an expected and
actual cell. Nothing in the test moves.

This page is the whole of that integration. The samples come from
[`src/Bobcat.Xunit.Samples/Specs/NarratedSpecs.cs`](https://github.com/JasperFx/bobcat/blob/main/src/Bobcat.Xunit.Samples/Specs/NarratedSpecs.cs),
and the output blocks are what that host printed.

## Turning it on

```xml
<PropertyGroup>
  <BobcatProjectAssertions>true</BobcatProjectAssertions>
</PropertyGroup>
```

That is a property the `Bobcat.Generators` package reads at build time. It is **off by default**: a
suite that never asked gets no projected assertions and no change in behaviour. A project that
references Bobcat by `ProjectReference` rather than by package also needs
`<CompilerVisibleProperty Include="BobcatProjectAssertions" />`, as the sample corpus does.

Only a project that records specifications sees any effect: the test has to be in a `[BobcatFeature]`
class, or carry `[BobcatSpec]`, the same as for any other step
([What you have to add](marker-steps.md#what-you-have-to-add)).

## What a projected assertion renders as

Four assertions under one marker comment, two of them wrong on purpose:

<<< ../src/Bobcat.Xunit.Samples/Specs/NarratedSpecs.cs#projected-assertions{cs}

```
  every assertion in a run is evaluated FAILED
  ───────────────────────────────────────────────
    ✗ Then  the calculator agrees about its value (12ms)
      ✓ Then  calculator.Value should be 3 (1ms)
      ✗ And   calculator.Value should be greater than 10 (9ms)
          ✗ calculator.Value: should be greater than '10', got '3'
      ✗ And   calculator.Value should be less than 2 (1ms)
          ✗ calculator.Value: should be less than '2', got '3'
      ✓ And   calculator.Value should be 3
  Failed with Rights: 2, Wrongs: 2, Errors: 0
```

Three things are happening there:

- **Each call is a step**, and its sentence is built from the subject and the assertion:
  `calculator.Value should be greater than 10`. The subject is the C# expression as written, so a
  well-named local reads well and `x.Y.Z` reads as `x.Y.Z`.
- **A run of consecutive assertions is all evaluated.** A plain Shouldly test throws at the first
  failure and never reaches the other three. Here all four run, two fail, and the test fails once,
  at the end of the run, with both wrongs in the failure message.
- **A cell says which comparison it made.** The bound in `ShouldBeGreaterThan(10)` is not an
  expected value, so the cell reads `should be greater than '10', got '3'` and never
  `expected '10', got '3'`.

### The run ends at the next action

The gathered failures are thrown at the end of the run of assertions, which is the point just before
the next action. That action would be operating on state the assertions have already shown to be
wrong, so it never runs and everything after it renders as not reached:

<<< ../src/Bobcat.Xunit.Samples/Specs/NarratedSpecs.cs#run-before-next-action{cs}

```
  the run throws before the next action FAILED
  ───────────────────────────────────────────────
    ✗ Then  the value disagrees (21ms)
      ✗ Then  calculator.Value should be 99 (21ms)
          ✗ calculator.Value: expected '99', got '3'
    · When  the calculator is doubled anyway — declared
    · Then  this is never reached — declared
      ○ Then  calculator.Value should be 6 — not run
  Failed with Rights: 0, Wrongs: 1, Errors: 0
```

## Which assertions get a cell

A cell is an expected value beside an actual one plus the comparison between them, and the
comparison comes from a closed set. These Shouldly methods map onto it:

| Shouldly | The cell says |
|---|---|
| `ShouldBe(x)` | `expected 'x', got '…'` |
| `ShouldBe(x, tolerance)` | `should be approximately 'x', got '…'` |
| `ShouldNotBe(x)` | `should not be 'x', got '…'` |
| `ShouldBeGreaterThan(x)` / `ShouldBeGreaterThanOrEqualTo(x)` | `should be greater than 'x', got '…'` |
| `ShouldBeLessThan(x)` / `ShouldBeLessThanOrEqualTo(x)` | `should be less than 'x', got '…'` |
| `ShouldContain(x)` | `should contain 'x', got '…'` |
| `ShouldStartWith(x)` / `ShouldEndWith(x)` | `should start with 'x'`, `should end with 'x'` |
| `ShouldBeNull()` / `ShouldNotBeNull()` | `should be null`, `should not be null` |
| `ShouldBeEmpty()` / `ShouldNotBeEmpty()` | `should be empty`, `should not be empty` |

The four that are not equality, side by side:

<<< ../src/Bobcat.Xunit.Samples/Specs/NarratedSpecs.cs#comparison-cells{cs}

```
  a cell says which comparison it made FAILED
  ──────────────────────────────────────────────
    ✗ Then  the value fails four different comparisons (25ms)
      ✗ Then  calculator.Value should be greater than 10 (23ms)
          ✗ calculator.Value: should be greater than '10', got '3'
      ✗ And   calculator.Value should be less than 2
          ✗ calculator.Value: should be less than '2', got '3'
      ✗ And   calculator.Value should not be 3 (1ms)
          ✗ calculator.Value: should not be '3', got '3'
      ✗ And   calculator.Value should be 3.5, 0.01
          ✗ calculator.Value: should be approximately '3.5', got '3'
  Failed with Rights: 0, Wrongs: 4, Errors: 0
```

**An assertion outside that set still renders as a step, with its verdict and duration, and no
cell.** `ShouldBeTrue`, `ShouldBeFalse`, `ShouldBeEquivalentTo`, `ShouldBeOfType`, `ShouldBeInRange`,
`ShouldAllBe` and the rest have no expected value to put beside an actual one, so any cell would state
something false. Shouldly's own failure message is reported instead:

<<< ../src/Bobcat.Xunit.Samples/Specs/NarratedSpecs.cs#no-cell{cs}

```
  an assertion bobcat cannot describe still renders as a step FAILED
  ────────────────────────────────────────────────────────────
    ✗ Then  a comparison outside the closed set reports no cell (14ms)
      ✗ Then  (calculator.Value > 10) should be true (14ms)
        False
            should be
        True
            but was not
  Failed with Rights: 0, Wrongs: 1, Errors: 0
```

The set is closed on purpose. A free-text comparison would let a producer say something no reader
can render or check, and the same closed set is what travels on the monitor wire and in the JSON
results file, so every consumer agrees about what a cell claims
([A cell says what it compared](monitor-design.md#a-cell-says-what-it-compared-issue-384)).

## Only a statement is projected

`x.ShouldNotBeNull().Name.ShouldBe("a")` consumes the first assertion's result. Gathering that first
call instead of throwing would dereference null and report a `NullReferenceException` in place of the
assertion that failed, so **a call whose value is used is left alone** and throws exactly as it
always did. A fluent chain is safe by construction; only a call standing on its own line becomes a
step.

The same rule keeps a `ShouldNotBeNull<T>()` or `ShouldBeOfType<T>()` whose result you keep as a plain
assertion. Write it as a statement when you want it as a step.

## Loops and branches

A projected assertion is a step **per call, not per line**. The generator plans one step for each call
site, and at run time every call records its own step, so the report shows what actually happened.

- **An assertion inside a loop renders once per iteration.** A `ShouldBe` inside a `foreach` over 60
  events gives 60 steps, each reading `And e.Version should be 1`. Nothing collapses them: each one
  really ran, and a failing iteration needs its own row to say which value was wrong. If that is not
  the specification you want to read, say it once over the whole collection, outside the loop:
  `events.ShouldAllBe(e => e.Version == 1)` is one step, with no cell and Shouldly's own message
  listing the items that did not match.
- **An assertion in a branch that did not run renders as `○ … — not run`**, greyed out, even in a
  green scenario. The call site was planned and never reached, the same as a step after a failure. It
  is not a failure and does not change the verdict.

## Shouldly inside an expected object

Projected assertions are one of two places Shouldly meets Bobcat. The other is a **member check** on
a partial object, for a claim about one member that is not equality. The partial object below holds
for a shipment weighing more than 3 whose tracking number starts with `1Z`, and says nothing about
its other members:

<<< ../src/Bobcat.Tests/Partial/CheckingMembersTests.cs#member-checks{cs}

Hand it to any step that takes an expected object, `ThenEvents`, `ThenReadModel` or `ThenMatches`,
and the report shows each check in the member table as it reads, `should be greater than 3`, with
the assertion's own message when it fails
([Expected Objects](expected-objects.md#member-checks-and-the-c-version-they-need)). Two rules there are
worth knowing before you reach for it: a check is one assertion on one member path, so a chained
Shouldly call is the build error BOBCAT044, and before C# 14 an expression tree cannot leave out
Shouldly's optional `customMessage` argument. And `ShouldBe` over a collection member
[stays ordered](expected-objects.md#shouldly-s-shouldbe-over-a-collection-stays-ordered), because that
is what Shouldly's own `ShouldBe` means, with `ignoreOrder: true` as Shouldly's own opt-out.

## Not Shouldly?

`SpecAssert.Gather(action)` runs any assertion that throws, from Shouldly, xUnit, NUnit or anything
else, and records the failure on the step in progress instead of ending the test. Only an assertion
exception is gathered; anything else is rethrown, because swallowing a `NullReferenceException` would
turn a broken test into a merely red one
([Checks that gather instead of throwing](marker-steps.md#checks-that-gather-instead-of-throwing)).

**Shouldly is the dialect Bobcat supports.** `IAssertionDialect` in the generator is the seam, and
FluentAssertions is the intended second. `Assert.*` is not planned: its subject is an argument whose
position differs per assertion, so a sentence built from one rule reads backwards, and a table of
every xUnit assertion is a maintenance burden with no ceiling.

## See also

- [Writing Specifications](marker-steps.md) -- step helpers, marker comments and `SpecAssert`, the
  other ways a step arrives
- [Specifications with Code](tutorials/specifications-with-code.md) -- the tutorial, which starts from
  a Shouldly test and narrates it
- [Expected Objects](expected-objects.md) -- partial objects, member checks and collection members
