# Bobcat with Marten

::: warning This integration is being rebuilt
`Bobcat.Marten` **was removed on 2026-09-21.** Nothing ships in its place yet.

The support is being rebuilt from what real applications turn out to need, rather than from what
an abstraction seemed like it should offer — so this page will describe the replacement once the
samples have shown what that is. It is a placeholder until then.
:::

## What it used to do

`MartenResource` — an `ITestResource` owning an `IDocumentStore`, defaulting its
between-scenario reset to deleting all document and event data while keeping the schema — plus
`MartenEntities` and `IStepContext` extensions for reaching the store from a step.

## Where the evidence is being gathered

`Bobcat.CritterStack` still ships the store-agnostic half: `EventStores`, `DocumentStores`,
`EventStoreAuthoring`, `RecordBuilding` and the `IStepContext.EventStore(...)` seam, all bound to
the **JasperFx.Events** abstractions rather than to Marten. Anything rebuilt here has to stay on
those abstractions, so the same code serves Marten, Polecat and Fisher.

The samples that use Marten now configure it themselves and reset through
`ResetEventStoresAsync()`.

## In the meantime

Use the library directly. A Bobcat resource is four members — `Name`, `StartAsync`, `StopAsync`,
`ResetBetweenScenarios` — and teardown is `StopAsync`, so a resource that owns nothing else needs
no disposer at all. See [Resources](../resources.md) for the contract and
[The Run Lifecycle](../run-lifecycle.md) for when each verb is called.
