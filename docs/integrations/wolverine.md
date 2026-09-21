# Bobcat with Wolverine

::: warning This integration is being rebuilt
`Bobcat.Wolverine` **was removed on 2026-09-21.** Nothing ships in its place yet.

The support is being rebuilt from what real applications turn out to need, rather than from what
an abstraction seemed like it should offer — so this page will describe the replacement once the
samples have shown what that is. It is a placeholder until then.
:::

## What it used to do

`IStepContext` extensions over Wolverine's tracked sessions
(`InvokeMessageAndWaitAsync`, `SendMessageAndWaitAsync`, `TrackActivity`, `ExecuteAndWaitAsync`),
`HandlerWarmUp` (priming the runtime compiler outside the tracked window, so the first act of a
run does not pay code generation), and `TransportDraining`.

It also carried the Wolverine half of `Bobcat.CritterStack`: `CritterStackFixture` and the shipped
Gherkin grammar — `When {command} is received`, `Then {event} is emitted` — plus `SagaGrammars`,
`HttpGrammars` and the tracked HTTP act.

## Where the evidence is being gathered

`samples/BankAccountES` is the case to watch. Two of its fixtures were **one line each**:

```csharp
public class FreezeAccountFixture : CritterStackFixture;
```

Between them they covered two feature files and declared two Event Model slices by type capture.
That sample is quarantined in `samples.yml` until the grammar is rebuilt, and it is the clearest
evidence in the repo of what the grammar was carrying.

The tracked-session helpers are expected to land in **Wolverine itself** rather than here.

## In the meantime

Use the library directly. A Bobcat resource is four members — `Name`, `StartAsync`, `StopAsync`,
`ResetBetweenScenarios` — and teardown is `StopAsync`, so a resource that owns nothing else needs
no disposer at all. See [Resources](../resources.md) for the contract and
[The Run Lifecycle](../run-lifecycle.md) for when each verb is called.
