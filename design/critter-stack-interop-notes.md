# Critter Stack interop notes (internal)

**Not published.** Hazards found while wiring Bobcat to Wolverine, Marten and Fisher hosts that are
**not Bobcat's behaviour** — they are upstream framework behaviour, or version-migration lore, that
a Bobcat suite happened to be the first thing to run into.

They are kept because each one cost real time and the diagnosis is written down. They are not in
`docs/` because documenting another framework's behaviour in Bobcat's manual makes Bobcat
responsible for keeping it current, and it will go stale the first time Wolverine or Marten changes
it.

If any of these is still biting people, the durable fix is a PR against that project's own docs.

## `(body, IResult)` tuple returns silently misrouted by Wolverine.HTTP
Wolverine.HTTP treats tuple returns as `(http-body, ...cascaded-messages)`. Returning
`(CreateStudentResponse, IResult)` cascades the `IResult` as a message with no handler, so the
endpoint returns the wrong status and logs `No routes can be determined for Envelope ...
HttpResults.Created<T>`.

- **Fix:** return `TypedResults.Created<T>(...)` directly instead of a `(body, IResult)` tuple.
- When the endpoint *also* cascades a message, the `IResult` goes **first**:
  `(Created<UserAccount>, UserCreated)`. The first tuple item is the HTTP response under
  Wolverine.HTTP's rules (an `IResult` is executed as-is), and everything after it is a cascaded
  message. `BookingMonolith` does this on all four of its creates.
- The rule is positional, and the other direction works: the **first** element is the response
  and may itself be an `IResult`, everything after it is cascaded. `EcommerceModularMonolith`'s
  checkout returns `(Accepted, BasketCheckoutEvent)` — a 202 with a Location, plus the event the
  Ordering module handles. The original `(bool, BasketCheckoutEvent)` "worked" too, in that it
  cascaded — it just answered every checkout with `true` and a 200.
- **When the endpoint also cascades a message**, it has to return a tuple, so `TypedResults`
  cannot be the whole return value either. Derive a record from `Wolverine.Http.CreationResponse`
  and put it in the first slot — `(ProposalCreation, MeetingGroupProposalAcceptedEvent)` — which
  gives the 201 and the `Location` header and still cascades the second element.
  `samples/MeetingGroupMonolith/Payments/CreateSubscription.cs` is the worked example. The body
  is then `{ id, url }` rather than the entity, so the fixture reads the created record back over
  a GET, which is the assertion the spec wanted anyway.
- This one is upstream in Wolverine, not Bobcat.

## Wolverine 6 no longer ships the runtime compiler
A host left in the default `TypeLoadMode.Dynamic` now fails to **start** with "no
`IAssemblyGenerator` (Roslyn) is registered" (Wolverine GH-2876) — the runtime compiler moved out
of core. Every sample carried over from WolverineFx 5.x has this.

- **Fix:** `<PackageReference Include="WolverineFx.RuntimeCompilation" />` (it auto-registers), or
  `opts.UseRuntimeCompilation()`, or pre-generate with `codegen write` + `TypeLoadMode.Static`.
- A build-only CI job cannot catch this. It takes running the specs, which is the argument for
  doing at least one sample end-to-end rather than declaring a sample fixed when it compiles.

## Marten projection subclasses must be `partial`, and `CreateEvent<T>` is gone
Two separate breakages in the same file, both from the Marten 9 / JasperFx.Events 2 move, and
only the first one is a compile error:

- **`CreateEvent<T>(e => …)` in a projection constructor no longer exists.** The supported form
  is the `Create` method convention: `public static TDoc Create(TEvent e) => …`.
- **A projection subclass that uses convention methods must be declared `partial`.** Marten
  dispatches `Create`/`Apply`/`ShouldDelete` through a compile-time source generator with **no
  runtime fallback**, and it emits into that class. Without `partial` the project compiles clean
  and the host fails to **start** with `InvalidProjectionException: No source-generated dispatcher
  found for …`. A self-aggregating type registered via `Snapshot<T>` does *not* need it — only a
  projection subclass does, which is why a sample can have several aggregates working and one
  projection that kills the host.

This is footgun 4's lesson a second time: a build-only CI job cannot catch either the start
failure or the drifted assertions behind it. `BankAccountES` compiled clean and could not start.

A smaller one from the same move, and this one *is* a compile error: `SnapshotLifecycle` now lives
in `JasperFx.Events.Projections`, not `Marten.Events.Projections`. Every sample of this vintage
that calls `opts.Projections.Snapshot<T>(SnapshotLifecycle.Inline)` needs the extra `using`.

**Revised on the JasperFx.Events 2.53.0 bump (issue #125).** The generator's rules moved, in the
right direction:

- A `SingleStreamProjection<,>` / `MultiStreamProjection<,>` subclass with convention methods
  **no longer needs `partial`** — its dispatcher is emitted as a standalone `file sealed class`
  registered through `[assembly: GeneratedEvolver(...)]` (jasperfx#462), so it never needs a
  second declaration of the user's type. `MartenWithProjectAspire`'s `TripProjection`
  (`ShouldDelete` conventions, not partial) builds and registers under 2.53.0.
- An `EventProjection` subclass with conventional `Create`/`Project` methods **still needs
  `partial`**, because its `ApplyAsync` dispatcher is an override emitted into the class — and the
  missing modifier is now **compile error `JFXEVT003`** rather than a start failure.
  `MartenWithProjectAspire`'s `DistanceProjection` was the one that tripped it on the bump.
- An `EventProjection` with an explicit `ApplyAsync` that is not partial gets warning `JFXEVT006`
  (published types not registered; storage still provisioned on demand).

So the build-only samples job now catches the projection half of this footgun; the "host does not
start" half is left to the runtime misconfigurations the job still cannot see.

## Fisher builds its schema lazily — apply it at startup, before the daemon and before the first append
Marten creates its event tables on the way into the first append. Fisher does not, quite: on a
fresh SQLite file the first `StartStream` through a Wolverine-integrated session with inline
projections reaches `AppendPlanner.ReadCurrentVersionAsync` before anything has created
`fi_streams` / `fi_events`, and the request fails with `SQLite Error 1: 'no such table: fi_streams'`.
`BankAccountES` on Fisher lost its first **three** scenarios that way, then passed the rest once
something else had built the tables — and passed 9/9 on the second run against the same file,
which is the worst kind of flake: it only shows on a clean checkout, which is where CI runs.

The async daemon has the same shape one layer down: `AddAsyncDaemon(DaemonMode.Solo)` registers a
hosted service that reads `fi_event_progression` as soon as it starts, so a host with an async
projection fails to **start** on a fresh file.

- **Fix, both cases:** `services.AddFisher(...).ApplyAllDatabaseChangesOnStartup()` — registered
  *before* `.AddAsyncDaemon(...)`, because hosted services start in registration order. It is
  what `Bobcat.CritterStack.Tests`' Fisher host does and what `BankAccountES` does. Marten has the
  same method and does not need it for this; calling it anyway is harmless.
- This is worth an upstream look (the first-append case reads like a Fisher bug — the planner
  should ensure storage the way `SaveChanges` does), but the sample does not wait on that.

## A host that runs on more than one store names the store in exactly one file
`BankAccountES` runs on Marten and on Fisher from the same handlers (`EventStore=Fisher` in
configuration or the environment). What made that possible was not Bobcat — it was rewriting the
host to the store-agnostic vocabulary Wolverine 6.26+ and JasperFx.Events 2.47+ ship, so that
`Program.cs` is the only file with a `using Marten` or `using Fisher`. The swaps, for the next
sample that wants the same property:

| Marten-specific | Store-agnostic | Lives in |
|---|---|---|
| `[AggregateHandler]` (`Wolverine.Marten`) | `[DeciderFunction]` | `Wolverine.Persistence.EventSourcing` |
| `[WriteAggregate]` / `[ReadAggregate]` | `[WriteModel]` / `[ReadModel]` | `Wolverine.Persistence.EventSourcing` |
| `IDocumentSession session` + `session.Events.StartStream<T>(...)` | return `Storage.StartStream<T>(id, events)` (a side effect; tuple with the response) or inject `IEventStoreOperations` | `Wolverine.Persistence` / `JasperFx.Events` |
| `IQuerySession.LoadAsync<T>` / `Query<T>()` | `IDocumentReadOperations.LoadAsync<T>` / `Query<T>()` + `JasperFx.Events.Documents.ToListAsync` | `JasperFx.Events.Documents` |
| `IQuerySession.Events.AggregateStreamAsync<T>` | `IEventStoreOperations.AggregateStreamAsync<T>` | `JasperFx.Events` |
| `class X : SingleStreamProjection<Doc, Id>` (`Marten.Events.Aggregation`) | a self-aggregating `Doc` with `Create` / `Apply` conventions, registered with `Snapshot<Doc>(SnapshotLifecycle.Inline)` | JasperFx.Events conventions; every store has `Snapshot<T>` |
| `[Entity]` | unchanged — it was always store-agnostic | `Wolverine.Persistence` |

Things that bit on the way:

- A `(Created<T>, StartStream)` tuple **works** from a Wolverine.HTTP endpoint: the first member
  is the response, the side effect runs in the same transaction. Footgun 3's `(body, IResult)`
  trap is about cascading *messages*; an `ISideEffect` is recognized as such.
- `Apply(IEvent<FundsDeposited> e)` on a self-aggregating document works on both stores and is the
  right way to get the event's timestamp into a read model (the projection this replaced stamped
  `DateTimeOffset.UtcNow`, so a rebuild would have re-dated history).
- `using Marten;` and `using Fisher;` in the same file (only `Program.cs` needs both) is fine as
  long as nothing names `StoreOptions`, `IDocumentStore` or `IDocumentSession` explicitly — the
  `AddMarten(opts => ...)` / `AddFisher(opts => ...)` lambdas infer them.
- `JasperFx.Events.Documents.ToListAsync` and Marten's own `ToListAsync` collide only if a file
  imports both namespaces; the endpoints import only the JasperFx one.

