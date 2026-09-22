# Composing Grammar Modules

A fixture's vocabulary comes from three places: the steps it declares itself, the steps of its
base classes (derive from `WolverineCritterStackFixture` and the whole event-sourcing grammar binds with
no further code), and **grammar modules** composed in with `[IncludeGrammars]`. This page covers
the module route: parameterizing a module, letting composed grammars cooperate at runtime, and
how an act of your own composes with the shipped assertions.

The compile-time discipline holds throughout. The generator reads every module's steps from the
type symbol, so an unmatched step is still a compile error, type captures still resolve at
compile time (BOBCAT011/012), and an ambiguous step is still BOBCAT013.

## Parameterized modules

Constructor arguments after the module type flow to the module's construction:

```csharp
[IncludeGrammars(typeof(DocumentGrammars))]
public class CreditWallet : WolverineCritterStackFixture;
```

The module is constructed **once per scenario**, like every module. The rules:

- Attribute literals bind positionally to the constructor's value parameters, in declaration
  order. Attribute arguments are constants and `typeof` only — a CLR restriction that is also
  the design: anything richer (a configured resource, a delegate) should reach the module from
  the scenario by type, through service injection or [scenario state](#scenario-state).
- Constructor parameters the literals do not cover are resolved **like step parameters**:
  `IStepContext`, any registered `ITestResource`, and services from the scenario's DI scope —
  `[FromRootService]` / `[FromKeyedServices]` work on a module constructor too. A module whose
  constructor asks for any of these is constructed by the first step that uses it, inside the
  scenario scope; a literal-only module is constructed when the plan is built. Either way it is
  one instance per scenario.
- Trailing optional parameters may be omitted.

Arguments no public constructor can take are a compile error, **BOBCAT019**, naming the
mismatch.

`[IncludeGrammars]` is discovered on the fixture *and its base classes*, so a shipped abstract
fixture can carry its modules and a derived fixture composes them by deriving alone. Declaring
the same module type on a derived fixture **re-parameterizes** it — the most-derived declaration
wins, the way a derived step hides a base one.

### One instance per module type

The same module type twice on one fixture is a compile error, **BOBCAT018**. Two instances of
one vocabulary — two `DocumentGrammars` bound to different stores — would make every one of its
step texts match twice, which is exactly the ambiguity BOBCAT013 exists to close, and Gherkin
has no section construct to scope the collision away. A fixture that genuinely needs two HTTP
surfaces declares two thin module subclasses with distinct step texts ("When the wallet API
receives …" / "When the admin API receives …") — one line each, and the spec reads better for
it.

## Scenario state {#scenario-state}

Composed grammars are separate instances, so they cannot cooperate through fixture fields. The
sanctioned channel is the typed per-scenario blackboard on `IStepContext`:

```csharp
context.SetState(new ActExecution(...));      // the acting grammar publishes its capture

if (context.TryGetState<ActExecution>(out var execution)) { ... }   // optional read
var execution = context.GetState<ActExecution>();                    // required read
```

One entry per CLR type, alive for exactly one scenario bracket — a retry attempt starts blank,
and nothing leaks between scenarios. Two grammars from two packages cooperate by agreeing only
on the capture *type*: no reference between them, no shared base class. `GetState<T>()` on a
missing entry throws with "no step in this scenario produced a T — did you mean to add a When …
step?", which is a far better failure than a silently-null field.

The Critter Stack vocabulary already rides it: every act step publishes its `ActExecution`
(the tracked session, the events the current stream gained, or the captured failure), the Given
steps publish the `ScenarioStream` being arranged, and every store assertion reads the published
capture — which is what lets a *different* grammar's act feed `Then {event} is emitted`
unchanged.

### What "the events the act appended" means when no stream was arranged (issue #319)

With a `ScenarioStream` published, it is that stream's delta across the act: fetch before, fetch
after, take the tail. That is the right answer whenever the scenario named a stream.

A slice whose act **creates** a stream has no name to give — the id is the handler's to mint, which
is the ordinary shape for an automation triggered by an upstream flow. That case used to yield the
empty list, so `Then {event} is emitted` reported

```
Expected a LedgerRegistered event, but the emitted events were: []
```

while the store held a complete stream. The message reads as "the handler did nothing", which is
the most misleading thing it could have said, and it made a whole slice shape unspecifiable: the
only way through was to make the handler derive its stream id from the act's payload so the
scenario could name it in advance — a *test harness* dictating a *design* decision.

So with no stream bracketed, the appended events are whatever the store issued during the act,
wherever it put them: a sequence floor taken before, queried after. Two whole-store reads, paid
only by the case whose alternative was an assertion that could not fail. Suites with no event
store at all — the message-only and HTTP lanes — take neither, and still see the empty list.

One consequence worth knowing: `Then no events are emitted` in a scenario that arranges no stream
used to be vacuously true. It now means what it says.


## The document lane: `DocumentGrammars`

The shipped Critter Stack vocabulary assumed event sourcing. Measured on a real document-backed
Wolverine application — `Storage.Insert`, `[Entity]`, a revisioned document, no event store — exactly
**four of the ten** shipped steps applied, and all four were the messaging and HTTP halves. Nothing
could arrange a document, assert one, or say it was gone, so such a project had to write a private
grammar before it could write its first scenario (issue #270).

`DocumentGrammars` is that lane, as a module:

```csharp
[FixtureTitle("Shipments")]
[IncludeGrammars(typeof(DocumentGrammars))]
[IncludeGrammars(typeof(DocumentGrammars))]
public class ShipmentsFixture : WolverineCritterStackFixture;
```

Three steps:

```gherkin
Given documents of type Shipment
  | Id                                   | Origin | Destination | Status |
  | 11111111-1111-1111-1111-111111111111 | Dallas | Austin      | Booked |

Then the Shipment with id "11111111-1111-1111-1111-111111111111" has
  | Origin | Status |
  | Dallas | Booked |

Then no Shipment exists with id "33333333-3333-3333-3333-333333333333"
```

The assertion compares **only the columns the row names** — the rule #241 established for events,
followed rather than replaced with a second convention. A document has fields the scenario does not
care about, and demanding a column for each makes the table say things the scenario does not mean.
The arrange is partial for the same reason; a column matching nothing is still refused by name.

Three things worth knowing:

- **`{document}` is a capture word of its own**, beside `{type}`/`{aggregate}`/`{command}`/
  `{event}`/`{readmodel}`/`{message}`. It resolves a type name exactly as the others do, but it
  stamps **no Event Modeling role** — a document-backed application has no stream, and putting an
  aggregate or read model on the canvas for it would describe nothing. Inert by construction: the
  emitter switches on the role words and lets this one fall through, as it does `{type}`.
- **The base class above is for the messaging vocabulary, not for event sourcing.**
  the base fixture's stream steps simply go unused; `Then {message} is sent` and the refusal
  checks work with no stream at all. `DocumentGrammars` itself derives from `Fixture`, so a project
  that only wants documents composes it onto a bare fixture and references no event-sourcing
  vocabulary.
- **Store-agnostic, like everything else here.** The steps reach the store through
  `JasperFx.Events.Documents`, so Bobcat still references no Marten, no Polecat and
  no Fisher. Nothing extra has to be registered either: on every Critter Stack store the concrete
  store object is both `IEventStore` and `IDocumentSessionFactory`, so the document steps resolve
  what the event steps already resolve.

`DocumentStores` is the public helper behind them — `LoadAsync(store, type, id)` closes the
generic `LoadAsync<T>` over a runtime `Type`, which every grammar taking a type-name capture
otherwise has to reflect for itself. Use it rather than hand-rolling, for the same reason as
`RecordBuilding` below.

**Not covered: saga state.** A Wolverine saga is a different storage surface from a document, and
asserting one needs its own vocabulary rather than a `{document}` in disguise. Tracked separately.

## Building an object from a table row

A grammar that takes a `StepTable` almost always has to turn each row into an object, and
`Bobcat.CritterStack.RecordBuilding` (in Bobcat core) is that conversion — the same one every shipped grammar
uses, and public API for exactly this reason (issue #272):

```csharp
[Given("shipments exist")]
public void GivenShipments(StepTable rows)
{
    foreach (var shipment in RecordBuilding.BuildAll(typeof(Shipment), rows,
                                                     "Given shipments exist", partial: true))
    {
        // …
    }
}
```

`Build` takes one header → cell map; `BuildAll` does one object per row. Records land on their
primary constructor, a settable-property object is the fallback, and cells convert with the same
rules a Gherkin literal uses everywhere else in Bobcat.

Two arguments are worth passing rather than defaulting:

- **`step`** — the step text, so a failure names the step the reader has to go and fix, not just
  the type.
- **`partial`** — `true` when the step is *arranging* history and `false` when it is performing an
  act. A `Given` names the fields the behaviour depends on and leaves the rest of a six-field event
  alone; a command's fields **are** the scenario's input, so a missing one is a spec that tests
  something other than what it says (issue #241).

Use it rather than hand-rolling. A private `bindRow` in each module is how the type coercion, the
treatment of a blank cell, and the message when a column matches nothing diverge once per
consumer — the divergence #241 spent effort removing from the shipped grammars. Sharing the helper
means a hand-written grammar and a shipped one fail the same way over the same table, including
the "the column [Wieght] matches nothing on 'Shipment'" typo message, the empty-unmatched-cell
rule that lets one table carry rows of several shapes, and the trailing-optional rule that lets a
column be omitted when the constructor has a default.

## The HTTP lane, and why it is not here

The shipped grammar's `When {command} is received` dispatches over the message bus. An HTTP slice
whose endpoint *is* the handler — appending in one transaction and returning an honest status —
needs an HTTP act instead, and Bobcat used to ship one: an `HttpGrammars` module, a
`CritterStackHttpFixture` base, and an `IHttpResource` transport seam so the grammar could drive a
call without referencing ASP.NET.

All three were deleted with `Bobcat.Alba` and have not come back, because nothing has needed them
since. What replaces them today is an ordinary act on your own fixture:

```csharp
[When("the wallet is credited")]
public Task CreditWallet(decimal amount)
    => WhenTracked(() => Context!.PostJsonAsync<Credit, Wallet>("/api/wallet/credit", new Credit(amount)));
```

`WhenTracked` ([Bobcat.Wolverine](integrations/wolverine.md)) runs the call inside the tracked
session, so the whole assertion vocabulary above works unchanged afterwards, and
`PostJsonAsync` is [Bobcat.Alba](integrations/alba.md)'s. The difference from the deleted lane is
that the step sentence is yours rather than shipped — so the slice's trigger kind is not inferred
from the grammar, and an HTTP-driven scenario stamps its slice the same way any other does, by
`@slice:` tag.

If the shipped HTTP vocabulary earns its way back, it will be because a spec wanted it.
