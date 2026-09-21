# Bobcat with Wolverine

`Bobcat.Wolverine` supplies **the act**: it sends a message into your application through
Wolverine's tracked session, and returns when everything that message *caused* has settled —
cascades, forwarded events, local queues drained. Your assertions then run against a system that
has finished reacting.

```csharp
public class FreezeAccountFixture : WolverineCritterStackFixture;
```

That one line is a whole fixture. Everything a scenario says comes from the shipped Critter Stack
grammar:

```gherkin
Scenario: Freezing an account records the freeze
  Given no events for Account "77777777-7777-7777-7777-777777777777"
  And events for Account
    | Event         | AccountId                            | Currency |
    | AccountOpened | 77777777-7777-7777-7777-777777777777 | USD      |
  When FreezeAccount is received
    | AccountId                            | Reason          |
    | 77777777-7777-7777-7777-777777777777 | Suspected fraud |
  Then AccountFrozen is emitted
  And the Account read model contains
    | IsFrozen |
    | true     |
```

## What this package is, and is not

**Only the `When` needs Wolverine.** Arranging events, asserting what was emitted, asserting a read
model — all of that is store work, lives in [`CritterStackFixture`](../composing-grammars.md) in
Bobcat core, and runs against the JasperFx.Events abstractions. This package adds one member:

```csharp
protected override async Task<IActOutcome> DispatchAsync(object command, int timeoutInMilliseconds)
    => new WolverineActOutcome(await Ctx.InvokeMessageAndWaitAsync(command, HostResource, timeoutInMilliseconds));
```

It also gives you `WhenTracked`, for an act that reaches the application from *outside* — an Alba
HTTP call, a SignalR client, a gRPC call — run inside the same tracked session so the assertion
vocabulary works unchanged afterwards:

```csharp
await WhenTracked(() => Context!.PostJsonAsync<FreezeAccount, Account>("/accounts/freeze", command));
```

A bare HTTP call returns when the response does, while the downstream work is still in flight.
This one returns when the work has landed.

Plus `WarmUpWolverineHandlers`, a hosted service that compiles every handler before the first
scenario so no act — and no scenario's timing — pays for code generation. Register it *after* the
resource whose host it warms; one registration order spans resources and services.

## Why the split

The package was deleted, along with `Bobcat.Alba` and `Bobcat.Marten`, so the support could be
rebuilt from what applications turn out to need. What the deletion exposed was a measurement:

**`CritterStackFixture` was 895 lines, and 11 of them mentioned a Wolverine type.** All eleven were
reading two things off a tracked session — the messages it sent, typed or untyped. So ~1,850 lines
had been coupled to a message bus for the sake of eleven, and a Marten or Polecat application with
no bus at all could not use the arrange-and-assert vocabulary.

Those two readings are now
[`IActOutcome`](https://github.com/JasperFx/bobcat/blob/main/src/Bobcat/CritterStack/ActOutcome.cs),
a two-member interface in core. The grammar went back to core behind it; this package implements
it. `samples/BankAccountES` — quarantined while the grammar was gone — runs 20 scenarios again on
**both** Marten and Fisher, unchanged.

What has **not** come back: the saga grammar (`Then the {saga} saga is …`) and the HTTP grammar
(`When {command} is posted to …`). Both were deleted with the package, nothing has needed them
since, and they return when a spec asks.

## See also

- [Composing Grammar Modules](../composing-grammars.md) — the store half of the vocabulary
- [Bobcat with Alba](alba.md) · [Bobcat with Marten](marten.md) · [Resources](../resources.md)
