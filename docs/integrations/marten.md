# Bobcat with Marten, Polecat and Fisher

**There is no `Bobcat.Marten` package, and you do not need one.** Bobcat reaches an event or document
store through the **JasperFx.Events** abstractions, which Marten, Polecat and Fisher all register --
so the same specification runs against any of them, and nothing in your test project has to name
Marten at all.

`CritterStackScenario` and `CritterStackSpec`, in `Bobcat.CritterStack` (shipped inside the `Bobcat`
package), are the store-only Given/When/Then vocabulary: arrange events and read models, act through
your application's own services, and assert what the act appended, what the aggregates and read
models became, and how the act was refused. **No message bus is involved.** Plenty of applications use
Marten without Wolverine, and this is the vocabulary for them. WolverineFx.Bobcat's `WolverineSpec`
derives from it and adds acts through Wolverine's tracked session.

## A specification

```csharp
using Bobcat;
using Bobcat.CritterStack;
using Microsoft.Extensions.DependencyInjection;

[BobcatFeature("Wallets")]
public class crediting_a_wallet(AppFixture app) : CritterStackSpec(app.Host)
{
    [Fact]
    public async Task a_credit_is_recorded()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Wallet>(id, new WalletOpened(id, "Ann"));

        await WhenActing("the wallet is credited",
            services => services.GetRequiredService<Wallets>().Credit(id, 10));

        ThenEvents(Specify<WalletCredited>().With(x => x.Amount, 10m));
        await ThenAggregate<Wallet>(id, Specify<Wallet>().With(x => x.Balance, 10m));
    }
}
```

The constructors take an `IHost`, or an `IServiceProvider` for a test that has a container and no
host (then `Host` throws, and every step reads `Services`), plus an optional `storeName` naming the
default store of an application with several. An application with one main store and ancillary ones needs no
`storeName`: the main store is the default ([Several event stores](#several-event-stores)). Don't want a base class? `new CritterStackScenario(host)` is
the same vocabulary as an object, and `CritterStackSpec` only delegates to one.

**Your application configures its store however it already does.** The spec project boots it -- in an
xUnit assembly or collection fixture, say -- and Bobcat finds the store in the container. Changing
`Program.cs` from Marten to Fisher is the only change it takes to run the same specifications against
the other store; `samples/BankAccountES` runs its specifications on both.

## The store-only act

`WhenActing(description, act)` runs any act an application performs -- a domain service, an endpoint
called in-process, a background job run by hand -- handed a fresh service scope. What it appended is
read back from the store afterwards: the act takes the store's high-water mark before it runs, reads
every event appended since, and keeps the ones on the specification's own streams -- those its Givens
arranged, `WhenActingOn` addressed, or one of its acts wrote to or started (bobcat#503). The rest are
`LastAct.ForeignEvents`. An exception it throws is captured, not rethrown, so a refusal can be asserted
with `ThenValidationFails` or `ThenRefusedWith`.

Two consequences of reading by sequence, worth designing around:

- **Whole-store assertions on a store-only act need serial execution on a shared store, or a store or
  tenant per test. For real parallelism, use `WolverineScenario`.** Attribution by stream drops what
  another test appends to *its* streams, but a stream another test *starts* while the act runs looks
  exactly like one the act started, and still counts. A whole-store step (`ThenEvents`, `ThenNoEvents`,
  `ThenStreamIsStartedWithEvents`, …) that fails while foreign events were seen says "cross-talk
  suspected" in its message; a passing one says nothing. `samples/BankAccountES` runs serially, with
  `[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]`. A `WolverineScenario` reads its act
  from Wolverine's tracked session, which knows exactly what the act committed.
- **No store reads an event's tags back**, so a step that judges Dynamic Consistency Boundary tags
  after a store-only act fails and says why, rather than reporting every tagged event missing.

When an act's events can't be attributed that way at all -- several writers, or a stream someone else
also appends to -- name the stream and the version instead, with `TheStreamVersion` and
`ThenEventsOn<T>(id, afterVersion, …)`, or `WhenActingOn<T>` for the whole paragraph in one call. See
[A named stream from a named version](wolverine.md#a-named-stream-from-a-named-version).

A Wolverine application usually wants to act through the bus or over HTTP instead. Derive from
`WolverineSpec`, or write your own act on a base spec class the way BankAccountES does; see
[Your own acts](wolverine.md#your-own-acts).

## The vocabulary

The whole vocabulary -- every Given, When and Then, the values each one takes, and the settings -- is
documented once, on [Bobcat with Wolverine](wolverine.md). Everything there that isn't marked as a
Wolverine or HTTP act, a message step, a boundary step or `ThenEventuallyCaused` is `CritterStackSpec`'s:

| | |
|---|---|
| [Arrange](wolverine.md#arrange) | `GivenEvents`, `GivenContext`, `GivenNoEventsFor`, `GivenEventsOn`, `GivenReadModel`, the clock steps |
| [Act](wolverine.md#act) | `WhenActing`, `WhenActingOn` |
| [Events](wolverine.md#assert-events) | `ThenEvents`, `ThenEventsInAnyOrder`, `ThenEmitted`, `ThenNotEmitted`, `ThenNoEvents`, `ThenEventsOn`, `ThenStreamIsStarted`, `ThenStreamIsStartedWithEvents` |
| [Aggregates, documents and read models](wolverine.md#assert-aggregates-documents-and-read-models) | `ThenAggregate`, `ThenDocument`, `ThenReadModel`, `ThenNoReadModel`, `ThenSingleReadModel`, `ThenSingleDocument`, `Verify`, `ThenMatches` |
| [Refusals](wolverine.md#assert-refusals) | `ThenValidationFails`, `ThenRefusedWith`, `ThenCommandRefused` |
| [DCB models](wolverine.md#dynamic-consistency-boundaries) | `ThenEventsOn(EventTagQuery, …)`, `TheDcbModel`, `ThenDcbModel` |

Every step that reads a projection waits for the async daemons first, up to `ProjectionTimeout` --
**20 seconds** by default, which is what a cold daemon on a loaded CI runner needs. A wait that runs out
is an error, a `ProjectionCatchUpTimeoutException` naming the projections that were behind and what was
being read, never a failed spec ([details](wolverine.md#assert-aggregates-documents-and-read-models)).

## Several event stores

An application with an ancillary store -- Marten's `AddMartenStore<IInvoicingStore>()`, Polecat's
`AddPolecatStore<T>()`, Fisher's `AddFisherStore<T>()` -- is specified in one scenario, whichever store a
step is about (bobcat#504).

<!-- JEREMY -->

**Each step goes to the store that owns its types.** Bobcat reads what each store registers through the
same JasperFx abstractions it reads everything else through: the aggregate and document types of its
projections, the events they apply and the event types it has registered, its DCB tag types
(`IEventStore.TryCreateUsage`), and its document mappings (`IDocumentStoreUsageSource.TryCreateUsage`).
`GivenEvents<Invoice>`, `ThenAggregate<Invoice>` and `ThenDocument<InvoiceSummary>` go to the store that
registers `Invoice` and `InvoiceSummary`; a Given with no aggregate type goes where its events are
registered; `TheDcbModel<T>` goes by `T`, then by the query's event and tag types.

- **One store claims the type:** that store.
- **None, or several:** the default store -- the constructor's `storeName`, else the only store, else the
  one main store -- when it can take it (it is among the claimants, or nobody claims the type).
- **Otherwise** the step throws a `SpecCriticalException` listing the stores that claim the type and
  telling you to name one. A stream addressed by its id alone (`TheStreamVersion(id)`) has no type, so it
  is read from the default store.

**The main store is the default.** With no `storeName`, a host whose stores include exactly one *main*
store -- the one `AddMarten`, `AddPolecat` or `AddFisher` registered, beside any number of
`AddMartenStore<T>()`-style ancillary ones -- uses that store as the default, for `Store`, for
`services.EventStore()` and for every step that falls back to the default. That is what makes an application
that embeds CritterWatch (`opts.AddCritterWatchEmbedded(...)`, which registers the console's own ancillary
store) specifiable unchanged. Bobcat tells a main store from an ancillary one by its registration, not its
name: a main store is the instance its product's `IDocumentStore` service resolves to, while an ancillary
store is registered under its marker interface only. With no main store, or more than one (say `AddMarten`
and `AddFisher` in one host), there is no default: name it with `storeName`, or address each step with
`In<TStore>()` / `In("name")`.

What a store registers is read once per scenario. Stores register some types lazily -- an event type the
first time it is appended, a document type the first time it is stored or loaded -- so a type you only ever
store by hand is claimed by nobody until something has stored it.

**Name a store when the type doesn't say.** `In<TStore>()` addresses the store registered under a marker
interface, and `In("name")` the store whose `IEventStore.Identity` name matches, ignoring case (`"main"`
for Marten's primary store; an ancillary store is named after its marker type, `"iinvoicingstore"`). Each
returns a view with the same Given and Then steps, pinned to that store. The view shares the scenario's
specification: what it arranges is the scenario's, an act through it is the scenario's act, and its steps
render in the same specification. `CritterStackSpec` has `In` too.

```csharp
await GivenEvents<Wallet>(wallet, new WalletOpened(wallet, "Ann"));          // the main store registers Wallet
await GivenEventsOn<Invoice>(invoice, new InvoiceRaised(invoice, 40m));      // IInvoicingStore registers Invoice
await In<IInvoicingStore>().GivenReadModel<SharedNote>(note);                // both register SharedNote

await WhenActing("the invoice is paid from the wallet", services => /* ... */);

ThenEventsInAnyOrder(new WalletDebited(wallet, 40m), new InvoicePaid(invoice));
await ThenAggregate<Invoice>(invoice, Specify<Invoice>().With(x => x.Paid, true));
```

**An act is observed on every store.** The act takes a high-water mark on each store, reads back what each
one gained, and attributes each store's events to the specification as a single-store act does.
`LastAct` lists them store by store (sequences belong to a store, so there is no order across stores), and
`LastAct.StoreOf(e)` says which store an event came from. A failing whole-store step adds an
"Appended by store -- 'main': …; 'iinvoicingstore': …" line.

**Waits cover the stores the specification involves.** `AfterAsyncDaemonsCatchUpAsync` waits on the default
store and on every store the specification touched -- a step went to it, a view was pinned to it, or an act
appended the specification's events there -- so a store it never involves, such as CritterWatch's, whose
daemon may never catch up in a test host, cannot hold it up or time it out. With no default store it waits on
every store. A view waits on its own store, and a read waits on the store it reads.

A host with one store behaves exactly as before. Nothing is read about what it registers.

## Below the vocabulary

The store helpers the vocabulary is built from are public, and all of them are store-agnostic:

| | |
|---|---|
| `services.EventStore(storeName?)` / `services.EventStores()` | the registered store: the only one, the one named, or the one main store among several |
| `services.MainEventStores()` / `services.IsMainEventStore(store)` | the stores registered as an application's main store (`AddMarten`, `AddPolecat`, `AddFisher`), not under a marker interface |
| `host.ResetEventStoresAsync()` | empty **every** store the host registers -- documents *and* event streams. The snapshots are documents, the events that produced them are not, and deleting one half leaves the other |
| `host.WaitForNonStaleProjectionsAsync(…)` | wait for the async daemon rather than sleeping |
| `EventStores.FetchStreamAsync` / `AggregateStreamAsync<T>` / `FetchLatestAsync<T>` | a stream's events, the aggregate rebuilt from them, the latest projected snapshot |
| `EventStores.QueryEventsSinceAsync` / `HighWaterSequenceAsync` | the store's tail |
| `EventStores.ProjectionProgressAsync` | per-shard progress, for when a wait times out |
| `DocumentStores.LoadAsync` / `StoreAllAsync` | documents by type |
| `EventStoreAuthoring.AppendAsync` | append arranged history |

## Why there is no package

<!-- JEREMY -->
`Bobcat.Marten` existed and was deleted, so that support could be rebuilt from what applications turn
out to need rather than from what an abstraction seemed like it should offer. It never came back.
Seven of nine sample suites had hand-written a Marten-specific reset --
`DeleteAllDocumentsAsync` plus `DeleteAllEventDataAsync` -- that Bobcat already shipped
store-agnostically. Swapping it for `host.ResetEventStoresAsync()` left every sample scenario passing
and took `using Marten` out of every spec project.

## See also

- [Bobcat with Wolverine](wolverine.md) -- the vocabulary reference
- [Writing Specifications](../marker-steps.md) -- step helpers, `SpecAssert`, tables, and slice binding
