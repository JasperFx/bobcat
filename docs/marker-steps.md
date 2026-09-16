# Specs from tests you already have

Issue #110. The other two authoring styles ask you to write a specification: a `.feature` file
bound to a fixture, or a [code-first `Specification`](code-first-specs.md). This one asks for
almost nothing. You point Bobcat at tests that already exist, in whatever runner they already use,
and they start reporting themselves as specifications — ordered steps, a `{Feature}/{Scenario}`
identity, and live progress in the [viewer](monitor-design.md).

The constraint that shaped it: a large existing suite has to be able to adopt this **one class at
a time**, without a base class, a signature change, or a rewrite. Anything more expensive than
that and the answer is always "not this quarter".

Marten's `DaemonTests` is the first real subject ([JasperFx/marten#5363](https://github.com/JasperFx/marten/pull/5363)) --
eighteen tests, no test body edited.

## Two ways in, and they compose

**Marker comments** declare the steps of one test:

```csharp
[Bobcat.BobcatFeature("Async daemon")]
public class when_the_daemon_catches_up : DaemonContext
{
    [Fact]
    public async Task the_projection_catches_up()
    {
        // Given the events are published
        await PublishEvents();

        // the daemon polls on its own schedule, which is why the wait below exists
        await WaitForNonStaleAsync();

        // When the projection daemon is running
        using var daemon = await StartDaemon();

        // Then every expected aggregate matches
        await CheckAllExpectedAggregatesAgainstActuals();
    }
}
```

Three of those comments are steps and one is a comment. A marker is a `//` comment opening with
`Given`, `When`, `Then`, `And` or `But` as a whole word — everything else stays invisible, which
is the property that makes this usable on a real suite full of explanatory comments.

**`[BobcatStep]`** goes the other way. Decorate a shared helper once and *every* test that already
calls it renders that step:

```csharp
[BobcatStep("the events are published on {threads} threads", Keyword = "Given")]
internal Task PublishMultiThreaded(int threads) => …
```

`{threads}` is filled in from the call site, so one attribute renders `PublishMultiThreaded(3)` as
"Given the events are published on 3 threads". Only literal arguments are substituted — a step
reading "published on threadCount threads" would be worse than one that visibly did not resolve.

The two compose. Comments give a test its narrative; decorated helpers give real per-step timing
across every test in the suite that touches them.

### They are different steps, and they nest (issue #305)

When a decorated helper is called inside a comment-declared region, **both** describe the same
stretch of the test — and the answer is not a precedence rule, because they are not the same kind
of claim. The comment is the author's sentence about *this* test. The attribute reports an
*operation*, wherever it is called from. So the comment is the row, and the helper renders
underneath it with its own keyword and its own verdict:

```
Given the events are published                    52ms
    Given the events are published on 3 threads   40ms
    Given the events are flushed                  12ms
When the projection daemon is running             declared
```

Neither keyword overrides the other, because they never occupy the same line. A helper called
where no comment has been written yet — before the first marker, or from a method that is not a
test — belongs to no sentence and renders on its own.

### What a declared step can say about itself (issue #304)

A declared step reports **the work observed inside it**: the steps that ran under it, their
verdicts, and the sum of their durations. It reports nothing else, and a region with no decorated
helper inside it stays blank — marked `declared`, with no duration and no verdict. Nothing
observed it, and a number borrowed from its neighbours would be exactly the inference this feature
promises not to make.

The attribution is decided **by the generator, from the call site's line**, against the comments
in the same method. Not from a stack trace, not from a clock: an interceptor is generated per call
site, so at build time which sentence a call sits under is an exact fact, and at runtime it would
be a guess. An index that does not match the comments actually registered — a stale `obj/`, a
class the feature attribute never marked — attributes to nothing rather than to the wrong
sentence.

## What you have to add

| | |
|---|---|
| Packages | `Bobcat`, `Bobcat.Generators`, and the adapter for your runner |
| A runner adapter | `Bobcat.Xunit` or `Bobcat.TUnit` |

That is the whole list. `Bobcat.Generators` brings the interceptor opt-in with it, so there is no
csproj line to remember -- and no sibling project that silently needed the same one.

Marker comments reach the runtime through a module initializer, which is why opting in really is
only comments -- nothing in a test body could have been made to carry them, and an assembly with no
marked class gains no initializer and no startup cost.

### `[BobcatStep]` helpers cannot be `protected`

A C# interceptor must be an extension method, and an extension method cannot see a `protected`
member. So every decorated helper has to be `internal` or `public`. This is a language rule rather
than a Bobcat design choice, and it is the one real cost of the attribute half — widening eight
helpers on `DaemonContext` is exactly what Marten's PR did. Marker comments have no such
constraint.

### The runner adapter

Add the package for your runner and put `[BobcatScenario]` on the test class:

```csharp
using Bobcat.Xunit;      // or Bobcat.TUnit

[BobcatFeature("Async daemon"), BobcatScenario]
public class DaemonSpecs
{
    [Fact]
    public async Task the_daemon_catches_up()
    {
        // Given events are published
        ...
    }
}
```

It works on a single method too. Both packages open a scenario around each test and close it with
**the verdict the runner reported** -- a failing test is published as a failure, and a skipped one
is withdrawn rather than reported as anything.

Bobcat deliberately shipped no adapter at first, on the theory that forty lines were cheaper than
guessing at a runner. That was wrong, and the reason is worth stating: those forty lines carry four things only
Bobcat knows, and getting any of them wrong leaves a green suite looking exactly like a correct one.
Marten's hand-rolled copy -- the one this page used to invite you to paste -- got all four wrong.
It never set the verdict, so every scenario it ever published was a `CleanPass`. It minted its own
run id and dropped `BOBCAT_RUN_TAG`, so its evidence could not be attributed to whatever asked for
the run. It published a `RunStarted` it might not own, and never a `RunFinished`. And its
interceptor opt-in was a csproj line that a sibling project also needed, where forgetting it is a
`CS9137` build failure rather than a missing feature.

**xUnit v2 has no adapter and is not simply waiting for one.** v2's `BeforeAfterTestAttribute` is
`Before(MethodInfo)` / `After(MethodInfo)` with no test context anywhere, so there is no verdict to
read at that point at all -- reporting one would mean replacing the test framework rather than
hooking it. v3 and TUnit both hand the result over at the end of a test, which is what makes their
adapters small.

## Declared is not executed

Two different things, kept apart on purpose:

- **Declared steps** are what the test *says* it does, read from marker comments at compile time
  and known before a line of it runs. That is what lets a scenario announce "step 2 of 4" up
  front, and it is trustworthy precisely because it was never inferred from what happened.
- **Recorded steps** are what actually ran, with a duration and a verdict. They come from
  `[BobcatStep]` interceptors.

`DeclaredSteps.For("{Feature}/{Scenario}")` reads the first at runtime; every `DeclaredStep`
carries the source line its comment came from.

## What reaches the viewer

A scenario publishes `ScenarioStarted` (with the declared step count and, since #304, the
declared sentences themselves), `StepStarted` as each step **opens** — carrying
`DeclaredStepNumber`, the sentence it ran under — `StepFinished` with that step's own verdict, and
`ScenarioFinished`. Steps are announced when they open rather than when they end, because a
watcher looking at a run in flight needs the step that is currently taking the time — which is
exactly the one that has not finished yet.

The narrative rides on `ScenarioStarted` rather than arriving as steps, and that is the wire
shape of "declared is not executed": publishing a declared step as a `StepStarted` would be
announcing that it ran.

With no viewer listening the publisher is null and the whole thing costs a few strings per test.

## A step need not be a keyword — `// *` (issue #324)

Bobcat's rendering does not depend on Given / When / Then, and should not. The inspiration is
ThoughtWorks' **Gauge**, whose specifications are bulleted sentences with no keyword vocabulary at
all, and **Storyteller**, whose specs read as prose rather than as a keyword table. Plenty of steps
are simply not one of five words:

```csharp
[Fact, BobcatScenario]
public async Task the_overnight_sweep_reconciles_every_wallet()
{
    // * two wallets, one of them credited twice
    await Seed();

    // * the overnight reconciliation runs
    await Sweep();

    // Then every balance agrees with its events
    await AssertBalances();
}
```

Renders as three steps, the first two carrying no keyword at all. Keywords still work and still
mean what they did — this is an addition, not a replacement, and the two mix freely in one test.

**Why the bullet is required.** Most comments in a test body are not steps, so treating every comment
as one would bury the real steps in noise. `*` is one character of opt-in, unmistakably deliberate,
and the same character Gauge uses. `// just explaining the next line` stays a comment.

The runtime needed no change for this: `DeclaredStep.ToString()` already omitted an empty keyword
rather than emitting a leading space. What was missing was any way to *write* one — and four
rendering sites that would have shown an empty `<strong>`.

## Binding a projected test to a slice — `[BobcatSlice]` (issue #324)

`[BobcatFeature]` makes a projected test *readable*. It said nothing about **which slice the test is
evidence for**, so the slice stayed unbound on the Event Model however green the test was, and no
spec-identity gate could join it. `[BobcatSlice]` closes that:

```csharp
[BobcatFeature("Booking appointments")]
[BobcatSlice(SliceType = typeof(ConfirmAppointment), Domain = "Scheduling", Chapter = "BookingAppointments")]
public class BookingSpecs
{
    [Fact, BobcatScenario]
    public async Task a_proposed_appointment_is_confirmed() { /* … */ }

    // One class, several slices: a method-level binding REPLACES the class's.
    [Fact, BobcatScenario]
    [BobcatSlice(SliceName = "ProposeHomeCheckAppointment", Pattern = "Automation")]
    public async Task an_accepted_assignment_proposes_a_visit() { /* … */ }
}
```

### Binding only — never roles

This says *which* slice. It never says what the slice's command, events or read models are. The
curated model states those on the **Declared** rung and the code states them on **Derived**; a third
opinion from a test can only manufacture a `SourceDisagreement` nobody can act on — see issue #323
for what that costs when it happens. A projected test's contribution is **evidence**: a bound
`{Feature}/{Scenario}` identity, which is what turns the slice from unbound to observed.

So a projected test's slice shows the roles the model and the code agree on, and the test's identity
alongside them. It merges by name with every other authoring style — a slice can carry Gherkin, HTTP,
code-first and projected specifications at once, because a slice is a vertical behaviour and not an
authoring style.

### Two spellings, and why `SliceType` is preferred

`SliceType` means **exactly** `SliceName = type.Name`. No suffix stripping, nothing inferred — which
is what makes it safe, because passing `typeof(ConfirmAppointmentHandler)` plainly would not produce
the slice name rather than being quietly "corrected".

Prefer it for two reasons. It is rename-safe, and — the one that matters more — **a type survives to
the generator where a string does not**, so only `SliceType` can later be cross-checked against what
the model declares.

Reach for `SliceName` where no type bears the slice's name. That is not an edge case: measured across
CritterCrush's nineteen slices, Command slices matched a type 13/13 and View slices 3/3, while
**Automation slices matched 0/3** — an automation's only type is `{Slice}Handler`, and it has no
command type at all, by design.

### Three diagnostics, so the preference is enforced rather than remembered

| | |
|---|---|
| **BOBCAT023** (error) | Both spellings set, naming *different* slices. One of them is wrong and no silent winner is the right answer. Setting both to the *same* slice is redundant, not wrong, and is allowed |
| **BOBCAT024** (warning) | `SliceName` is a literal string and a type of that name exists in this compilation — use `SliceType`. Silent where no such type exists, which is every Automation |
| *(not a generator diagnostic)* | "`SliceType.Name` matches no slice in the model" cannot live here: the generator has no curated model to check against. It belongs to the model merge, where a binding naming nothing shows up as an unmatched slice |

### A method-level binding replaces, it does not override

A rebinding is a *whole* binding. An earlier cut merged the method's tags over the class's, and a
method rebinding to another slice dragged the class's `Domain` / `Chapter` / `Pattern` with it —
stamping them on a slice that already had its own answers. So a test that wants the class's domain as
well restates it.

## Saying a slice is specified here — the spec-ownership manifest (issue #324)

`[BobcatSlice]` binds a test that already exists. The manifest is the other half: it says, **before
any code exists**, that a slice is going to be specified as a projected test, so the scaffolder
writes a skeleton for it instead of a `.feature`.

It is a separate file from the event model, and deliberately so. Moving a test is a change to where
work lives, not to the design record — putting it on the slice would churn the model, and its
byte-for-byte regeneration claim, every time a suite is reorganized. The two files join on `model:`,
the same merge key everything else folds by.

```yaml
schema: 1
model: CritterCrush                      # must match the event model's `model:`
slices:
  - slice: ProposeHomeCheckAppointment
    kind: unit
    authoring: projected
    owner: CritterCrush.Specs.ProposalSpecs
    coveredBy: HomeChecks/Accepting an assignment books the home check as an appointment
```

Name it `*.spec-ownership.yaml` and add it to the spec project's `AdditionalFiles` — that is the file
name the generator looks for, and the diagnostics below are silent without it.

### Absent means Gherkin

A slice the manifest does not list keeps today's behaviour exactly. So the file is purely additive —
CritterCrush needs **three entries, not nineteen** — and adopting it cannot silently change what an
existing repo scaffolds.

### `kind` and `authoring` are orthogonal

`kind` says whether the specs go through the database. `authoring` says how they are written. They
are independent, and the counter-example to collapsing them already ships: **Marten's `DaemonTests`
are `integration` + `projected`** — real database tests, rendered through marker steps.

| `kind` | `authoring` | what the scaffolder emits |
|---|---|---|
| `integration` | `gherkin` | a `.feature` — the default, and what every unlisted slice gets |
| `integration` | `code-first` | a `Specification` skeleton |
| `integration` | `projected` | **nothing** — an existing hand-written suite adopts the slice |
| `unit` | `projected` | a projected test skeleton |
| `unit` | `gherkin` *or* `code-first` | **invalid** — both run through the fixture, and so through the store |

That last row is a validation rule rather than a note: honouring the authoring would hand a
`.feature` back to an author who asked for a unit test. `kind: unit` on its own resolves to
`projected`, since that is the only pairing the format permits.

### `coveredBy`, because the rule would otherwise rot

"A unit-tested slice is fine as long as something runs the command end to end later" is a good rule
that dies the first time somebody deletes that scenario. Naming the cover makes it checkable, and it
is required exactly when `kind: unit`.

Inferring it is not realistic — the chain from `AcceptHomeCheckAssignment` through the bus into
`ProposeHomeCheckAppointment` is not expressible in the model, which is precisely why the declaration
is the honest mechanism. Scenario names themselves stay in the model: the manifest says only *where*
a slice is specified and *in what kind*, so a spec-identity gate reads identities from one file and
location from the other.

### It cannot be derived, so it is validated

A manifest keyed on slice names is exposed to the rot CritterCrush already paid for once: its
hand-written Stoat plan carried **eleven spec identities matching no scenario**, silently, because
nothing joined them. That plan could be fixed by deriving it. This file records a human choice and
cannot be, so validation is the only defence:

- `model:` matches the event model, every `slice:` exists in it, and no slice is listed twice.
- `coveredBy` names a `{Feature}/{Scenario}` the model actually declares.
- One `owner:` is one authoring style and one feature — `[BobcatFeature]` and `[FixtureTitle]` are
  both class-level, so a type cannot publish two of them.

### The join, checked in both directions

The manifest is the **forward** declaration; a slice tag in the code is the **backward** binding.
Checking only forward leaves a manifest quietly disagreeing with the suite; checking only backward
leaves a slice declared unit-tested that nobody ever wrote a test for.

| | |
|---|---|
| **BOBCAT025** (error) | Some spec source in this compilation specifies a slice in a different lane than the manifest declares. Two lanes means two specs claiming one `{Feature}/{Scenario}` identity |
| **BOBCAT026** (warning) | The manifest takes a slice out of the Gherkin lane and nothing in this compilation binds it. A warning rather than an error, because the owner may legitimately live in a sibling assembly |

BOBCAT025 is the duplicate-identity guard, and the route into it is not exotic: switch a slice to
`projected`, forget to delete the `.feature` the scaffolder wrote for it last time, and without this
nothing says a word.

### One thing the projected lane cannot spell

A projected test's scenario title **is its method name**, with underscores read as spaces — there is
no title attribute in that lane. So a scenario name with punctuation in it cannot round-trip: "an
assignment, once accepted, books a visit" becomes `an_assignment_once_accepted_books_a_visit`, which
publishes a *different* identity and joins nothing.

Reading the manifest warns about it, and the scaffolded skeleton says so in a comment above the
method. The fix is to rename the scenario in the model to something a method name can spell.

## The honest limits

- **A comment-declared step still has no clock of its own.** It reports the work observed *inside*
  it (issue #304) — the decorated helpers that ran under it, their verdicts, and the sum of their
  durations — and a region containing none of them says nothing at all. Timing needs somewhere to
  intercept and a comment does not give one; what changed is that the work underneath it is now
  attributed to it, exactly, from the call site's line.
- **A step outside a scenario is silent.** Decorated helpers get called from plenty of places that
  are not specifications, and reporting from them would be noise.
- **Test methods are matched by attribute name** — `Fact`, `Theory`, `Test`, `TestCase` — so the
  generator needs no reference to a runner it is trying to stay neutral about.
