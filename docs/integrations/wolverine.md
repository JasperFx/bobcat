# Bobcat with Wolverine

`WolverineFx.Bobcat`, published from the [Wolverine repository](https://github.com/JasperFx/wolverine),
is the Given/When/Then vocabulary for a Wolverine application: arrange events, act through
Wolverine's tracked session, and assert what the act appended, what it cascaded, and what the
aggregates and read models became. Every call is a step, and every step renders.

```bash
dotnet add package WolverineFx.Bobcat
dotnet add package WolverineFx.Bobcat.Http   # only for Wolverine.HTTP acts
```

It is built in layers, and this page is the reference for all of them:

| | Package | Adds |
|---|---|---|
| `CritterStackScenario` / `CritterStackSpec` | `Bobcat` | everything a store can answer: arranging events and read models, store-only acts, event, aggregate, document and read model assertions, refusals, clocks. No message bus. See [Bobcat with Marten, Polecat and Fisher](marten.md) |
| `WolverineScenario` / `WolverineSpec` | `WolverineFx.Bobcat` | acts through Wolverine's tracked session, message assertions, Dynamic Consistency Boundary steps, and `ThenEventuallyCaused` |
| `WolverineHttpScenario` / `WolverineHttpSpec` | `WolverineFx.Bobcat.Http` | `WhenPosted` through Alba, with the route resolved from the request type |

The `*Scenario` class holds the steps. The `*Spec` class is an abstract base for an xUnit or TUnit test
class that exposes every step of its scenario as its own member, so a test reads `await
WhenReceived(...)` rather than `await Scenario.WhenReceived(...)`. Every member is public, so your own
helpers in another class can drive a spec too, and `Scenario` is there when you need the object
itself.

## A first specification

```csharp
using Bobcat;
using Wolverine.Bobcat;

[BobcatFeature("Appointments")]
public class confirming_an_appointment(AppFixture app) : WolverineSpec(app.Host)
{
    [Fact]
    public async Task a_scheduled_appointment_is_confirmed()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id));

        await WhenReceived(new ConfirmAppointment(id));

        ThenEvents(typeof(AppointmentConfirmed));
        ThenMessageSent<SendConfirmationEmail>();
    }
}
```

`AppFixture` is your own -- an xUnit assembly, collection or class fixture that boots the
application's `IHost` once. See [Getting Started](../getting-started.md#_2-host-the-application-once).
`[BobcatFeature]` is what makes the test a specification; on TUnit, NUnit and MSTest put
`[BobcatScenario]` beside it (see [Writing Specifications](../marker-steps.md#what-you-have-to-add)).

**A wrong is gathered, not thrown.** Inside a recording scenario a failed `Then…` reports on its own
step and the test carries on, so a specification shows every disagreement, and the adapter fails the
test once at the end. Outside one -- the same test with no adapter -- the wrong is thrown
(`SpecificationFailedException` from a Wolverine scenario, `SpecAssertionException` from a
`CritterStackScenario`), so a plain test cannot pass by accident.

## Values a step takes

Most steps take events, commands or documents as `object`, and accept any of these:

| | |
|---|---|
| a whole object | compared **structurally** -- property by property, nested properties and collections included, never through `Equals` |
| `Expect.Value(x).Ignoring(e => e.OccurredAt)` | a whole object with members nobody controls shown but not judged |
| `Specify<T>().With(x => x.Prop, value)` | a **partial object**: judged and shown on only the members it names. Handed to a Given or a When, it is *built*, with every unspecified member filled by `UnspecifiedValues` |
| `Specify<T>(table)` | a partial object written as a table: `Property \| Value` rows, or the members as headers over one row |
| `Specify<T>(x => x.Age.ShouldBe(4), …)` | a partial object to check against, written as assertions on its members. **Needs C# 14** -- see below |
| `typeof(T)` | the type alone, whatever its values (`ThenEvents(params Type[])`) |
| `Tagged(value, tags…)` / `Specify<T>().Tagged(tags…)` | an event expected (or arranged) with exactly these Dynamic Consistency Boundary tags |

`Specify`, `Tagged` and `Expect.Value` are static, so a `*Spec` class reaches them unqualified.
Prefer a deterministic clock to `Ignoring(...)` for a timestamp.

Every form, with what each one judges and how it renders, is [Expected Objects](../expected-objects.md).

::: warning A member check needs C# 14
`Specify<T>(x => x.Age.ShouldBe(4))` is an expression tree -- that is how Bobcat reads the member path
and the expected value out of it -- and before C# 14 an expression tree cannot leave out an optional
argument, which every Shouldly assertion has (`customMessage`). The error is **CS0854**, "An
expression tree may not contain a call or invocation that uses optional arguments".

On C# 12 or 13 (net9.0, or a pinned `LangVersion` -- Wolverine's own tests pin C# 12) either write
every optional argument out positionally, `x => x.Age.ShouldBe(4, null)`, or use the `.With(…)` chain
or a table instead. [The full table is in Expected
Objects](../expected-objects.md#member-checks-and-the-c-version-they-need).
:::

## Arrange

| Step | |
|---|---|
| `GivenEvents<TAggregate>(id, params events)` | the stream the scenario acts against already holds exactly these events. `id` is a `Guid`, a `string` key, or a strong-typed id around either |
| `GivenEvents(Type? aggregate, id, params events)` | the same with the type as a value. A null aggregate arranges a stream with no aggregate type |
| `GivenEvents(id, params events)` | a stream with no aggregate type, for an event model that names none |
| `GivenNoEventsFor<TAggregate>(id)` | the stream has no events yet. Appends nothing, says so, and names the stream the act's events are read from |
| `GivenEventsOn<TAggregate>(id, params events)` | events on a *different* stream from the one the act runs against -- a second aggregate for a rule that spans two, or history a read model fans in from |
| `GivenContext<TContext>(id, params events)` | a stream the act only *looks up* -- the Client an `OpenAccount` loads -- not the one it decides against. The same append and step as `GivenEvents`; on the Event Model it is the slice's `ReadsFrom`, never an aggregate (bobcat#502). Also `GivenContext(Type, id, params events)` |
| `GivenReadModel<T>(document)` | a read model already exists, stored directly. It bypasses the projection, so a spec about how the view is built should arrange the events instead. On the Event Model it is the slice's read model only on a View slice; before a command it is a lookup, the slice's `ReadsFrom` (bobcat#614). `T` is inferred from a typed document or a `Specify<T>()`: `GivenReadModel(new Flight { … })`. Give it explicitly only for a document held as `object` |
| `GivenTheDateIs(date)` / `GivenTheTimeIs(now)` | freeze the ambient clock (`BobcatClock.Current`, also `Clock`) |
| `WhenTheClockAdvancesBy(duration)` / `WhenTimePasses(duration)` / `WhenTheClockAdvancesTo(instant)` | move it forward. Each renders as its own step |

## Act

The **last** act a test runs is what every `Then…` judges. Nothing an act throws is rethrown: the
exception is captured on `LastAct`, so a refusal can be asserted afterwards.

| Step | |
|---|---|
| `WhenReceived(message, configureTracking?, options?)` | `message` arrives at the application through Wolverine, and the scenario waits for everything it caused to settle -- cascades, forwarded events, local queues. `options` is Wolverine's `DeliveryOptions`, so `new DeliveryOptions { TenantId = "red" }` sends it for a tenant |
| `WhenPublished(message, configureTracking?, options?)` | published to every subscriber, and the scenario waits for what it caused |
| `WhenTracked(description, act, configureTracking?)` | any act that reaches the application from outside -- an HTTP call, a client SDK, a hosted service poke -- run inside the tracked session |
| `WhenActing(description, services => …)` | any act at all, handed a fresh service scope. In a `WolverineScenario` it runs inside the tracked session too, so it waits for what it caused and carries the messages it sent |
| `WhenActingOn<T>(id, description?, act, params events)` | one paragraph of three sentences: capture the stream's version, run the act, and check the stream gained exactly `events` past that version |
| `WhenPosted(command, configure?)` (`WolverineHttpSpec`) | POST `command` to the one Wolverine.HTTP endpoint that accepts its type, filling route parameters from its properties. Several candidates are refused rather than guessed |
| `WhenPosted(command, route, configure?)` / `WhenPosted<TResponse>(…)` | POST to a route template; the generic form hands back the response body |
| `WhenPostedToRoute(command, routeName, configure?)` | POST to the endpoint with that ASP.NET Core route name (`[WolverinePost("/api/orders", RouteName = "PlaceOrder")]`) |
| `WhenPut` / `WhenPatched` / `WhenDeleted` (and `…ToRoute`) | the same three forms for PUT, PATCH and DELETE. A DELETE sends the command as its JSON body |
| `WhenReading<T>(route, params routeValues)` / `WhenReadingRoute<T>(routeName, …)` | GET a route template, its values after it in order, and hand back the 2xx body as a `T` -- null for a 4xx. A spec whose last act is a read describes a **Query** slice |

`configureTracking` adjusts the `TrackedSessionConfiguration` for one act. Over HTTP, any 2xx is
success, a 4xx is a *refusal* -- an answer, captured for `ThenRefusedWith` and `ThenResponseIs` --
and a 5xx is the act failing. The `Action<Scenario>` overloads apply Alba overrides (headers,
authentication) on top.

`WhenActing` in a `WolverineScenario` reads the act's events from what the tracked session heard
committed. When it heard no commit at all -- the act appended through a session Wolverine didn't
open -- it falls back to the store's high-water read, as the store-only act does.

## Assert: events

| Step | |
|---|---|
| `ThenEvents(params events)` | the act appended exactly these events, in order |
| `ThenEvents(params Type[] events)` | exactly these event types, in order |
| `ThenEventsInAnyOrder(params events)` / `(params Type[])` | exactly these, in any order |
| `ThenEmitted(params events)` / `ThenEmitted<T>()` | these among whatever else it appended; nothing else is judged, or shown |
| `ThenNotEmitted<T>()` / `ThenNotEmitted(params events)` | none of these. A partial object forbids only the events that agree on the members it names |
| `ThenNoEvents()` | the act appended nothing -- the refusal half of a guard |
| `ThenEventsOn<TAggregate>(id, params events)` / `ThenNoEventsOn<TAggregate>(id)` | exactly these on that stream, for a command that decides against several. Only what the act added counts |
| `ThenStreamIsStarted<TAggregate>(id)` | the act started that stream |
| `ThenStreamIsStartedWithEvents<TAggregate>(params events)` | one paragraph: a stream was started, with an id the handler assigned, and these events are on it |

**"The act's events" in a `WolverineScenario` are its own**: what the sessions handling the act's
message (or its HTTP request) committed. A cascade's events are in `LastAct.AllNewEvents`, not
`LastAct.NewEvents`. A store-only act can't tell the two apart, so there they are the same list.

**A store-only act attributes what it reads back by stream (bobcat#503).** An event counts when it is on
a stream the specification arranged (any Given), addressed (`WhenActingOn`), or that one of its acts wrote
to or started. Anything appended meanwhile to another stream that already existed is
`LastAct.ForeignEvents`: never judged by the whole-store steps above, and named by them when they fail --
"cross-talk suspected: N event(s) on streams this specification never arranged or started were appended
during the act". A step that names a stream (`ThenEventsOn<T>(id, …)`) or a tag query still sees what was
appended there. A stream the host seeds outside the vocabulary is foreign until the spec arranges or addresses it.

### A named stream from a named version

For an act whose events nothing can attribute -- a domain service, a background job run by hand, an
application with no message bus -- the spec names **which stream, and past which version**:

```csharp
await GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"), new WalletCredited(id, 10));
var before = await TheStreamVersion(id);                       // 2

await WhenActing("the wallet is credited by hand",
    services => services.GetRequiredService<WalletService>().Credit(id, 25));

await ThenEventsOn<Wallet>(id, before, new WalletCredited(id, 25));
await ThenNoEventsOn<Wallet>(id, before + 1);
```

| Step | |
|---|---|
| `TheStreamVersion(id)` | the version the stream stands at now; 0 with no events. Not a step |
| `ThenEventsOn<T>(id, afterVersion, params events)` | the stream gained exactly these, in order, past that version, whoever appended them |
| `ThenNoEventsOn<T>(id, afterVersion)` | it gained nothing |

A version past the stream's end is refused as a broken spec rather than passing vacuously.
`WhenActingOn<T>` does all three in one call and reads as one paragraph:

```
Given the Wallet stream Wallet is at version 2
When the wallet is credited by hand
Then WalletCredited is emitted on it
```

## Assert: aggregates, documents and read models

Every step that reads a projection first waits for the async daemons to catch up, up to
`ProjectionTimeout` (20 seconds by default). An async projection read straight after the act would
read a stale document.

**A wait that times out is an error, not a failed spec (bobcat#497).** A daemon that is behind, stopped
or not running says nothing about the document, so the step errors with a
`ProjectionCatchUpTimeoutException` (a `TimeoutException`). Its message names the wait, the projection
shards that were behind (`'WalletSummary:All' at 7 of 12`, when the store can say), the timeout, and the
type and id being read. The store's own exception is the `InnerException`. A store with no events at
all has nothing to be stale, so the wait returns at once there.
`AfterAsyncDaemonsCatchUpAsync()` times out the same way, with no read named (`ReadType` is null), and
your own waits outside the vocabulary get the same diagnosis from `EventStores.DiagnoseCatchUpTimeoutAsync`
([below the vocabulary](marten.md#below-the-vocabulary)).

- **`at N of M`: M is the highest event sequence number, not a count of events.** N is how far the shard
  has got. Deleting event data (Marten's `DeleteAllEventDataAsync`, say) does not reset the sequence, so
  67 events published after a wipe can read as `at 0 of 461`. Compare N with M, not M with the events
  you appended.
- **"The store could not say which projection was behind (…)"** means reading the shards' progress
  itself failed, and the parentheses carry that exception's type and message (also on
  `BehindFailure`, bobcat#686). A `42P01` "relation … does not exist" there usually means another
  process dropped or rebuilt the schema mid-run (a test sharing the database, a parallel lane), not
  that a projection is slow.

| Step | |
|---|---|
| `ThenAggregate<T>(id, expected?)` | the aggregate as the store serves it through `FetchLatest<T>`: a projected snapshot where there is one, else built from the stream. Throws when there is no such stream |
| `ThenReadModel<T>(id, expected?)` | the read model with this identity, loaded with `LoadAsync` once projections have caught up. Throws when there is none |
| `ThenDocument<T>(id, expected?)` | the same load, for a plain document or a projection stored as one |
| `ThenNoReadModel<T>(id)` / `ThenNoReadModel<T>()` | no such read model -- with that id, or at all |
| `ThenSingleReadModel<T>(expected?)` | the `T` of the stream this scenario is about -- the one the act started, else the one the givens arranged. Single-stream projections only. With no stream, the one `T` in the store, as below |
| `ThenSingleDocument<T>(expected?)` | the `T` document stored under that stream's id. With no stream (a document app, where the act's document gets its id from the endpoint or a cascaded handler), **the one `T` in the store**: none or several is a failure naming the count |
| `ThenProjectionsAreCaughtUp(Type readModel)` | the projections that build it have caught up |
| `AfterAsyncDaemonsCatchUpAsync(tenantId?)` | wait for every async daemon, on every database or one tenant's: the database the store's `FindDatabaseForTenantAsync` finds for it, else every database. Not a step |
| `ForTenant(tenantId)` | a view whose Givens append, and whose Thens read, as one tenant of a multi-tenanted store, on the database that tenant lives in ([details](marten.md#one-tenant-of-a-multi-tenanted-store)). Send the act for the tenant yourself, with `DeliveryOptions { TenantId = … }` |
| `Verify(subject, table)` | selected properties of anything, as a one-row table: `"\| Name \| Address.City \|\n\| Ann \| Austin \|"` |
| `ThenMatches(subject, expected)` | anything, structurally, by the same rules as `ThenEvents` |

`expected` is a partial object, a whole object or an `Expect.Value`, and each of these hands the loaded
object back for further checks.

## Assert: refusals

| Step | |
|---|---|
| `ThenValidationFails(reason)` | the act failed with `reason` |
| `ThenRefusedWith(reason)` | the same check, worded for any refusal |
| `ThenRefusedWith(reason, params mentioning)` | and the refusal names each of these values |
| `ThenRefusedWith<TException>(expected?)` | the act threw a `TException`, matching a partial object when one is given. Hands it back |
| `ThenCommandRefused()` | a *clean* refusal: the act ran, nothing threw, and it appended nothing -- a handler that stops without throwing, or an HTTP 4xx |
| `ThenRefusedWithProblem(expected?)` (`WolverineHttpSpec`) | the last HTTP act answered with a ProblemDetails body matching `Specify<ProblemDetails>()…` |
| `ThenResponseIs(status)` (`WolverineHttpSpec`) | the last HTTP act answered `status` |
| `ThenNotFound()` (`WolverineHttpSpec`) | the last HTTP act answered 404 -- a read of something that does not exist |

A reason is found in an HTTP refusal's body, or in the exception the act raised **and every inner
exception** (each of an `AggregateException`'s too), so a refusal wrapped by the act's plumbing is
still found. The match is a substring, **case-sensitive** (`StringComparison.Ordinal`). An empty
reason is refused, since every refusal contains one.

## Assert: messages

| Step | |
|---|---|
| `ThenMessageSent<T>(destination?)` | the act sent or published a `T` -- to `destination` when given, matched as a prefix of the URI (`"rabbitmq://queue/billing"`, `"local://"`) |
| `ThenMessageSent(expected)` | a message structurally equal to `expected` |
| `ThenMessageSentLocally<T>()` / `ThenMessageSentExternally<T>()` | to a local queue / to anything but one |
| `ThenMessagesSent<T>(count = 1)` | at least `count` of them |
| `ThenMessageScheduled<T>(delay?)` | scheduled for later, due no sooner than `delay` after the act. A scheduled message isn't sent within the act, so `ThenMessageSent` doesn't see it |
| `ThenNoMessageSent<T>()` / `ThenNoMessageScheduled<T>()` | none |

A `T` matches its subtypes too.

## Dynamic Consistency Boundaries

For a decision whose consistency boundary is a tag query rather than one stream (wolverine#4865):

```csharp
await GivenEvents<SeatAvailability>(theScreeningId.Value,
    Tagged(new ScreeningScheduled(theScreeningId, 10), theScreeningId));

await WhenReceived(new ReserveSeat(theScreeningId, theCustomerId, "4C"));

ThenEvents(Specify<SeatReserved>().With(x => x.Seat, "4C").Tagged(theCustomerId, theScreeningId));
ThenEventsOn(EventTagQuery.For(theCustomerId), Specify<SeatReserved>().With(x => x.Seat, "4C"));
await ThenDcbModel<SeatAvailability>(EventTagQuery.For(theScreeningId),
    Specify<SeatAvailability>().With(x => x.Seats, 9));
```

| Step | |
|---|---|
| `Tagged(event, tags…)` / `.Tagged(tags…)` | an event arranged or expected with exactly these tags, in any order. A tag left out is as wrong as a wrong one |
| `ThenEventsOn(EventTagQuery, params events)` / `ThenNoEventsOn(EventTagQuery)` | exactly these, in order, among the act's events the query selects. Arranged history never counts |
| `TheDcbModel<T>(query)` | `T` projected over the events the query selects; null when nothing matches. Not a step |
| `ThenDcbModel<T>(query, expected)` | and it matches `expected` |

Tag-judging steps need the tags the act appended, which the tracked session sees. **No store reads an
event's tags back**, so after a store-only act a tag-judging step fails and says why, rather than
reporting every tagged event missing.

### The boundary the decision loaded

The boundary steps (wolverine#4964) judge what the act's own `[DcbModel]` handler *loaded* -- the tag
query a concurrent append is checked against when the boundary is saved. They are deterministic:
nothing is appended and nothing races, because a hypothetical event is matched against the recorded
query.

```csharp
await WhenReceived(new ClaimBadge(theConference, theAttendee));

// Exactly the tag query the handler's Load() returned
ThenTheConsistencyBoundaryIs(EventTagQuery.For(theConference).AndEventsOfType<BadgeClaimed, BadgeRevoked>());

// Another badge claimed or revoked for the same conference would have conflicted...
ThenAConcurrentEventWouldConflict<BadgeClaimed>(theConference, new AttendeeId("ATT-99"));
ThenAConcurrentEventWouldConflict(Specify<BadgeRevoked>().Tagged(theConference));

// ...but not one for another conference, nor an event type the decision never read
ThenAConcurrentEventWouldNotConflict<BadgeClaimed>(new ConferenceId(Guid.NewGuid()), theAttendee);
ThenAConcurrentEventWouldNotConflict<SessionProposed>(theConference);
```

| Step | |
|---|---|
| `ThenTheConsistencyBoundaryIs(query)` | the act loaded exactly one boundary, and its query is exactly this: the same conditions, in any order, none missing and none extra |
| `ThenTheConsistencyBoundaryIs<TModel>(query)` | the boundary loaded for `TModel`, for an act that loads several |
| `ThenAConcurrentEventWouldConflict(event)` / `<TEvent>(params tags)` | another writer appending this between the act's read and its write would have made it fail with a concurrency conflict |
| `ThenAConcurrentEventWouldNotConflict(event)` / `<TEvent>(params tags)` | it falls outside every boundary the act loaded |

They're judged whether the act succeeded or was refused, because a refusal is decided over the same
boundary. `LastAct.Boundaries` holds what was loaded, in load order; a cascade's loads are not there.

### A concurrent writer

`WhileActing(params events)` arranges a real concurrent append (wolverine#4940): while the next act runs,
another writer appends these events between the act's read and its write, on a new stream. Tag them to put
them inside or outside a boundary. Inside, the act's save fails with a `DcbConcurrencyException`
(`ThenRefusedWith<DcbConcurrencyException>()`), or an error policy retries it and `ThenRetried()` passes;
outside, the act is not disturbed. It needs the store integrated with Wolverine (`IntegrateWithWolverine()`).
An act that saves nothing never gives the append a moment to land, and fails saying so.

## What the act eventually caused

The tracked session sees what happens inside the act. `ThenEventuallyCaused` (wolverine#4965) reaches
past it, to events appended across an asynchronous boundary -- a message an event subscription
published, an async projection's side effect:

```csharp
await WhenReceived(new ShipParcel(theParcel));

await ThenEventuallyCaused(new ParcelShipped(), new LabelPrinted(theParcel));
await ThenEventuallyCaused<LabelPrinted>();
```

Every act runs under a correlation id of its own (`ActCorrelationId`, `LastAct.CorrelationId`). Once
the async daemons have caught up, the step queries the event store for every event recorded with that
id, judged as `ThenEmitted` judges, and keeps looking for up to `EventuallyTimeout` (10 seconds by
default) for work that lands after the daemons.

Three things it needs from you:

- **The store has to record correlation ids**, which Marten, Polecat and Fisher all leave off by
  default -- on Marten, `Events.MetadataConfig.CorrelationIdEnabled = true`. When it doesn't, the step
  fails and says how to turn them on.
- **Every hop has to carry the id.** Wolverine's handlers do. An event subscription or a projection's
  side effect that publishes a message has to pass the event's `CorrelationId` on itself, as in
  `bus.PublishAsync(message, new DeliveryOptions { CorrelationId = e.CorrelationId })`.
- **An HTTP act sends the id as the `X-Correlation-ID` header.** For Wolverine.HTTP to apply it,
  turn on `WolverineHttpOptions.ApplyCorrelationIdHeaderToAllEndpoints`.

An act sent for a tenant is looked for within that tenant: filtered by it on a conjoined store, and in the
tenant's own database on a store that spreads its tenants across several. An act with no tenant on such a
store is looked for in every database.

## Reading what happened

None of these is a step: each reads the capture, and the assertion that follows is the step.

| | |
|---|---|
| `LastAct` | what the last act did: `Session` (the tracked session), `NewEvents`, `AllNewEvents`, `Error`, `Refusal`, `Appended` (each committing session with the message it handled), `Boundaries`, `CorrelationId`, `Attempts`, `TenantId` |
| `TheEvent<T>()` | the one `T` the act appended; throws unless exactly one was |
| `TheEvents` | every event the act appended |
| `TheMessagesSent` | every message the act sent or published |
| `TheAggregate<T>(id)` | the write model, folded from its stream |
| `TheStartedStream<TAggregate>()` / `TheStartedStreamKey<TAggregate>()` | the id the last act assigned to the stream it started |
| `TheStreamVersion(id)` | the stream's version now |
| `LastResponse` (`WolverineHttpSpec`) | the last HTTP act's Alba result |

## Settings

| | Default | |
|---|---|---|
| `ProjectionTimeout` | 20 s | how long a step that reads a projection waits for the async daemons |
| `Timeout` | 5 s | how long an act's tracked session waits for everything it caused to settle |
| `IncludeExternalTransports` | `true` | whether the tracked session also waits on external transports |
| `EventuallyTimeout` | 10 s | how long `ThenEventuallyCaused` keeps looking once the daemons have caught up |
| `UnspecifiedValues` | Bobcat's | what fills the members a partial object leaves unspecified when it is built for a Given or a When |

`Timeout`, `IncludeExternalTransports` and `EventuallyTimeout` live on `Scenario`. A
`WolverineScenario` and a `CritterStackScenario` take a `storeName` naming the default store of an
application with several event stores -- steps go to the store that owns their types, and `In<TStore>()` /
`In("name")` address one explicitly ([Several event stores](marten.md#several-event-stores)) -- and
`ResetAsync()` clears every store and forgets the last act.

## The Event Model, from the vocabulary

Every step above that names a command, an aggregate, an event, a read model, a document or a message
carries `[EventModelRole]`, and the HTTP act carries `[EventModelTrigger(TriggerKind.Http, Route =
nameof(route))]`. So a specification bound to a slice with `[BobcatSpec]` or `[BobcatSlice]` hands the
generator the slice's whole shape with nothing authored for the model:

```csharp
[BobcatFeature("Confirming appointments over HTTP")]
public class projected_http_slice(AppHost app) : WolverineHttpSpec(app.Host)
{
    [Fact]
    public async Task a_scheduled_appointment_is_confirmed_over_http()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, "Ann"));

        await WhenPosted(new ConfirmAppointmentRequest(id), "/api/appointments/confirm");

        ThenEvents(typeof(AppointmentConfirmed));
        ThenMessageSent<SendConfirmationEmail>();
    }
}
```

That slice reaches the model as an HTTP slice with its route, its command
`ConfirmAppointmentRequest` and the message it publishes. Negative steps (`ThenNoMessageSent<T>`,
`ThenNotEmitted<T>`, …), refusals, waits and `WhenTracked` stamp nothing. The DCB model steps stamp
an aggregate. See [Binding, not roles](../marker-steps.md#event-model-roles) for how the generator
reads them.

### Your own acts

A store-only scenario has no HTTP or message act of its own, and an application often wants its own
spelling of one. Write it on your base spec class, run it through `WhenActing`, and **re-declare the
attributes** -- the generator reads them off the method a test calls, never off what that method's
body calls. From `samples/BankAccountES`:

<<< ../../samples/BankAccountES/Tests/BankAccountSpec.cs#when-posted{cs}

<<< ../../samples/BankAccountES/Tests/BankAccountSpec.cs#when-received{cs}

An act can also address an endpoint by its route name: `[WolverinePut("/api/clients/{clientId}", RouteName =
"UpdateClient")]` on the endpoint, and `[EventModelTrigger(TriggerKind.Http, RouteName = nameof(routeName))]` on
the act. The generator resolves a constant route name to the endpoint's template at compile time. It also
matches an interpolated route against the endpoints' templates. See
[The act's route](../marker-steps.md#act-route). The route name is stamped as `TriggerOrigin.HttpRouteName`.

An act with a `GET` or `HEAD` trigger and a `ReadModel` role makes its slice a **Query** (bobcat#501, see
[Binding, not roles](../marker-steps.md#event-model-roles)). Until
[wolverine#4975](https://github.com/JasperFx/wolverine/issues/4975), Wolverine derives every GET chain as a
View, and a derived claim outranks a declared one. So the merged slice is a View with a Pattern
`SourceDisagreement` that drops the specs' Query. `samples/BankAccountES` pins that on purpose.

## Cold hosts and brokers

**Handler warm-up is automatic.** Under Wolverine's default `TypeLoadMode.Dynamic` a handler compiles
the first time a message reaches it, which on a cold host lands *inside* the first act's tracked
window. Every act warms the host first: by default (`HandlerWarmUp.Automatic =
AutomaticWarmUp.PrimeCompiler`) it compiles one chain, which pays the one-time cost of starting the
compiler. `AutomaticWarmUp.AllHandlers` compiles every chain, and `host.WarmUpHandlers()` does that
up front, before any specification, so no scenario's timing includes codegen. A chain that can't
compile is reported in one `HandlerWarmUpException`.

Wolverine.HTTP routes are not covered by that. Wolverine owns that switch:
`app.MapWolverineEndpoints(opts => opts.WarmUpRoutes = RouteWarmup.Eager)`.

**A broker is not reset with the store.** Messages a scenario cascaded to a real broker survive a
store reset and arrive inside the *next* scenario, failing it with an id that belongs to one that
already passed. `host.DrainTransportsAsync()` purges every broker queue the application declares;
call it wherever your suite already resets the store. It isn't automatic, because purging a broker
someone else is watching is destructive.

## See also

- [Bobcat with Marten, Polecat and Fisher](marten.md) -- the store-only half, with no message bus
- [Writing Specifications](../marker-steps.md) -- step helpers, `SpecAssert`, tables, and slice binding
