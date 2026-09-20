# Bobcat with Marten

`Bobcat.Marten` makes a [Marten](https://martendb.io) document and event store available to steps —
for arranging data directly, for asserting on what was stored, and for resetting between scenarios.

```bash
dotnet add package Bobcat.Marten
```

## Register the store

```csharp
runner.Suite.AddResource(new MartenResource(() => DocumentStore.For(connectionString)));
```

`MartenResource` takes an `IDocumentStore` directly, or a factory (sync or async), plus an optional
`name` and a `reset` hook. `ResetBetweenScenarios` runs that hook.

If your host already builds a store — an Alba-hosted app usually does — resolve it from the host's
services rather than constructing a second one.

## Arrange and assert from steps

```csharp
[Given("a customer named {string}")]
public Task Customer(string name) => Context!.StoreAsync(new Customer { Name = name });

[Then("the customer is stored")]
public async Task Stored() => (await Context!.QueryByIdAsync<Customer>(_id)).ShouldNotBeNull();
```

| Method | What it does |
|---|---|
| `QueryByIdAsync<T>(id)` | Load one document |
| `QueryAllAsync<T>()` | Every document of a type |
| `StoreAsync<T>(document)` | Store one |
| `DeleteByIdAsync<T>(id)` | Delete one |
| `FetchStreamAsync(streamId)` | The raw `IEvent` list for a stream |
| `AggregateStreamAsync<T>(streamId)` | The stream folded into an aggregate |
| `CleanAllMartenDataAsync()` | Delete the data, keep the schema |
| `CompletelyRemoveAllAsync()` | Drop everything |

All take an optional trailing `resourceName`.

The two stream methods are worth reaching for when a spec fails: `FetchStreamAsync` tells you what
the system actually appended, where an assertion on projected state only tells you the answer was
wrong. That distinction is the subject of
[Agent Friendly Integration Tests](../tutorials/agent-friendly-tests.md).

## Tables of documents — `[MartenEntities]`

`[MartenEntities]` is a grammar behaviour that turns a Gherkin table into stored documents, one per
row, opening a session for the step and committing at the end:

```gherkin
Given the customers
  | Name  | Status |
  | Ada   | Active |
  | Grace | Active |
```

The generic form `[MartenEntities<Customer>]` names the document type. See
[Data Intensive Specifications](../tutorials/data-intensive-specifications.md) for the table
mechanisms generally.

## Resetting between scenarios

A suite that does not reset passes on a clean database and then reports conflicts for records it
believes are new. Choose deliberately:

- **`CleanAllMartenDataAsync()`** — data gone, schema kept. Fast, and what most suites want.
- **`CompletelyRemoveAllAsync()`** — everything gone. Slower, and necessary when the schema itself
  is under test.

Note that a per-scenario reset clears the store but **not** a message broker — an outbox row is
gone while the queued message is not. See [Bobcat with Wolverine](wolverine.md).
