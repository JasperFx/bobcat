# Composing Grammar Modules

A fixture's vocabulary comes from three places: the steps it declares itself, the steps of its
base classes (derive from `CritterStackFixture` and the whole event-sourcing grammar binds with
no further code), and **grammar modules** composed in with `[IncludeGrammars]`. This page covers
the module route: parameterizing a module, letting composed grammars cooperate at runtime, and
the shipped HTTP lane (`CritterStackHttpFixture`) that is built from exactly these pieces.

The compile-time discipline holds throughout. The generator reads every module's steps from the
type symbol, so an unmatched step is still a compile error, type captures still resolve at
compile time (BOBCAT011/012), and an ambiguous step is still BOBCAT013.

## Parameterized modules

Constructor arguments after the module type flow to the module's construction:

```csharp
[IncludeGrammars(typeof(HttpGrammars), "/api/wallet")]
public class CreditWallet : CritterStackFixture;
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
one vocabulary — two `HttpGrammars` bound to different prefixes — would make every one of its
step texts match twice, which is exactly the ambiguity BOBCAT013 exists to close, and Gherkin
has no section construct to scope the collision away. A fixture that genuinely needs two HTTP
surfaces declares two thin module subclasses with distinct step texts ("When the wallet API
receives …" / "When the admin API receives …") — one line each, and the spec reads better for
it.

## Scenario state {#scenario-state}

Composed grammars are separate instances, so they cannot cooperate through fixture fields. The
sanctioned channel is the typed per-scenario blackboard on `IStepContext`:

```csharp
context.SetState(new TrackedExecution(...));      // the acting grammar publishes its capture

if (context.TryGetState<TrackedExecution>(out var execution)) { ... }   // optional read
var execution = context.GetState<TrackedExecution>();                    // required read
```

One entry per CLR type, alive for exactly one scenario bracket — a retry attempt starts blank,
and nothing leaks between scenarios. Two grammars from two packages cooperate by agreeing only
on the capture *type*: no reference between them, no shared base class. `GetState<T>()` on a
missing entry throws with "no step in this scenario produced a T — did you mean to add a When …
step?", which is a far better failure than a silently-null field.

The Critter Stack vocabulary already rides it: every act step publishes its `TrackedExecution`
(the tracked session, the events the current stream gained, or the captured failure), the Given
steps publish the `ScenarioStream` being arranged, and every store assertion reads the published
capture — which is what lets a *different* grammar's act feed `Then {event} is emitted`
unchanged.

## The HTTP lane: `CritterStackHttpFixture`

The shipped grammar's `When {command} is received` dispatches over the message bus, which only
binds when the command is a bus-visible message. The recommended default for an HTTP slice is
**collapsed** — the endpoint *is* the handler, appending in one transaction and returning an
honest status — and that shape needs an HTTP act. `CritterStackHttpFixture` is that lane, built
as an assembly of existing pieces: the store vocabulary via its `CritterStackFixture` base, the
`HttpGrammars` module composed on the class, and the tracked capture flowing between them over
scenario state. It declares no steps of its own.

```csharp
[IncludeGrammars(typeof(HttpGrammars), "/api/wallet")]   // optional: bind a route prefix
public class CreditWallet : CritterStackHttpFixture;
```

```gherkin
Given no events for Wallet "8f1c…"
When CreditWallet is posted to "/credit"
  | WalletId | Amount |
  | 8f1c…    | 25     |
Then the response is 200
And WalletCredited is emitted
And the WalletSummary read model contains
  | Balance |
  | 25      |
```

The HTTP steps:

- **`When {command} is posted to {string}`** — builds the command record from the (at most one)
  table row of body fields, POSTs it as JSON to the prefixed route, inside Wolverine's tracked
  session — so the step returns only when everything the call *caused* (cascades, local queues
  drained) has landed, and every store assertion below it reads what the call did. The JSON goes
  through the application's own serializer, so the spec's wire shape is the application's wire
  shape.
- **`Then the response is {int}`** — the status assertion. This is the HTTP lane's refusal
  vocabulary: an HTTP guard refuses with ProblemDetails/400, not an exception, so the
  caught-exception semantics of `Then validation fails with …` do not map. `Then the response
  is 400` composed with `Then no events are emitted` describes the sad path.

`HttpGrammars`' constructor takes `(routePrefix, hostResource, storeName,
timeoutInMilliseconds)`, all optional — name the host resource when a suite registers several.

### The transport

The suite must register a test resource implementing `Bobcat.Runtime.IHttpResource` — the seam
that carries the call. `Bobcat.Alba`'s `AlbaResource` (both forms) implements it over the
in-memory TestServer, so the usual Critter Stack wiring is already enough:

```csharp
runner.Suite.AddResource(new AlbaResource<Program>());
```

`Bobcat.CritterStack` itself still references no Alba and no ASP.NET: the grammar sees only the
`IHttpResource` contract, the same delegate-shaped composition that keeps `WhenTracked` free of
HTTP dependencies. Any other way of reaching the application — a real socket, a gRPC-web bridge
— plugs in by implementing the same two-record contract (`SpecHttpRequest` in,
`SpecHttpResponse` out; status codes are never asserted by the transport).

The `{command}` capture still resolves at compile time and still stamps the Event Modeling slice
— an HTTP-driven scenario and a bus-driven one tagged with the same `@slice:` fold into one
slice descriptor, because a slice is a behaviour, not a transport.
