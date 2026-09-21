# Bobcat with Alba

::: warning This integration is being rebuilt
`Bobcat.Alba` **was removed on 2026-09-21.** Nothing ships in its place yet.

The support is being rebuilt from what real applications turn out to need, rather than from what
an abstraction seemed like it should offer — so this page will describe the replacement once the
samples have shown what that is. It is a placeholder until then.
:::

## What it used to do

`AlbaResource` / `AlbaResource<TProgram>` — an `IHostResource` over Alba's in-memory
`IAlbaHost`, with content-root resolution, per-scenario DI scopes, restart support and console
log-level control — plus `IStepContext` extensions (`PostJsonAsync`, `GetJsonAsync`,
`PutJsonAsync`, `DeleteAsync`, `SendRawAsync`) and an `IHttpResource` transport seam that let a
grammar drive HTTP without referencing ASP.NET.

One piece survived into core: [`AlbaContentRoot`](../resources.md), which resolves the content
root the way `WebApplicationFactory` would but checked, and reports how it decided. It reads
`[WebApplicationFactoryContentRoot]` reflectively, so Bobcat core carries no ASP.NET reference.

## Where the evidence is being gathered

Nine samples under `samples/` host their application with Alba. Each now carries its own
`Tests/WebApp.cs` (a ~25-line `ITestResource`) and `Tests/AlbaSupport.cs` (the four call shapes
its fixtures need). They are **identical copies, deliberately** — what they have in common is the
measurement of what belongs back in a package.

Every one of them calls `AlbaContentRoot` on its first line, because raw `AlbaHost.For<Program>()`
guesses `<solution>/<assembly name>` and no sample sits there.

## In the meantime

Use the library directly. A Bobcat resource is four members — `Name`, `StartAsync`, `StopAsync`,
`ResetBetweenScenarios` — and teardown is `StopAsync`, so a resource that owns nothing else needs
no disposer at all. See [Resources](../resources.md) for the contract and
[The Run Lifecycle](../run-lifecycle.md) for when each verb is called.
