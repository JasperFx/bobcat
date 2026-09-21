# Bobcat with Marten

**There is no `Bobcat.Marten` package, and you do not need one.** Bobcat reaches an event or
document store through the **JasperFx.Events** abstractions, which Marten, Polecat and Fisher all
register — so the same spec code runs against any of them, and nothing in your test project has to
name Marten at all.

Your application configures Marten however it already does. Your spec project registers the host
and resets between scenarios:

```csharp
[BobcatConfiguration]
public static void Configure(BobcatRunner runner)
    => runner.Resources.Add(new AlbaResource<Program>(reset: host => host.ResetEventStoresAsync()));
```

`ResetEventStoresAsync` empties **every** store the host registers — documents *and* event streams,
which is what it takes: the snapshots are documents, the events that produced them are not, and
deleting one half leaves the other.

## What you get from a step

All of it store-agnostic, in `Bobcat.CritterStack` (shipped inside `Bobcat`):

| | |
|---|---|
| `Context.EventStore()` | the registered store, by name when there is more than one |
| `Context.FetchEventStreamAsync(id)` | the events on a stream |
| `Context.AggregateEventStreamAsync<T>(id)` | the aggregate rebuilt from them |
| `Context.QueryEventsSinceAsync(sequence)` / `HighWaterSequenceAsync()` | the store's tail |
| `Context.WaitForNonStaleProjectionsAsync()` / `WaitForProjectionAsync<T>()` | wait for the async daemon rather than sleeping |
| `Context.ProjectionProgressAsync()` | per-shard progress, for when a wait times out |
| `Context.ResetCritterStackAsync()` | the reset above, from inside a step |

`DocumentStores.LoadAsync` / `StoreAllAsync` cover documents by type, and `EventStoreAuthoring`
appends arranged history.

## Why there is no package

`Bobcat.Marten` existed and was deleted, along with `Bobcat.Alba` and `Bobcat.Wolverine`, so that
the support could be rebuilt from what applications turn out to need rather than from what an
abstraction seemed like it should offer. `Bobcat.Alba` came back, because nine sample suites each
wrote the same file. Marten did not, and the evidence is what decided it.

Seven of the nine samples had hand-written this in their spec project:

```csharp
var store = host.Services.GetRequiredService<IDocumentStore>();
await store.Advanced.Clean.DeleteAllDocumentsAsync();
await store.Advanced.Clean.DeleteAllEventDataAsync();
```

That is a Marten-specific spelling of something Bobcat already shipped store-agnostically. Swapping
it for `host.ResetEventStoresAsync()` left all 88 sample scenarios passing, and took `using Marten`
and `IDocumentStore` out of every spec project — so those suites would now run against Polecat or
Fisher by changing only the application's own `Program.cs`.

The old package also carried a `MartenResource` that owned an `IDocumentStore` with no host around
it. Nothing needed it: every sample has an application, and the store comes from the application's
container. It stays deleted until something asks.

**One thing did not come back and has no home.** `[MartenEntities]` was a persistence recipe that
bound Gherkin table columns to documents at compile time. Its sibling `[EfCoreEntities]` still
ships in `Bobcat.EntityFrameworkCore`, so the asymmetry is real — but no sample used either, and
writing a recipe nobody has asked for is the habit this exercise exists to break.

## See also

- [Resources](../resources.md) · [Bobcat with Alba](alba.md)
- [Composing Grammar Modules](../composing-grammars.md) — the document and event-store grammars
