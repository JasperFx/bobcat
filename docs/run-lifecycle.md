# The Run Lifecycle

Everything Bobcat does to your application happens on a fixed schedule. Knowing it answers most
"why did my suite do that" questions directly — why a scenario saw the previous one's data, why the
first scenario of a run is slower than the rest, why a teardown failure buried the error you
actually wanted.

## The whole run, in order

```
StartAll                    every resource, in registration order
  ↓
Preflight                   all checks run, results gathered
  ↓
Global SetUp                every IGlobalAction, in registration order
  ↓
  ┌─ per scenario ─────────────────────────────────┐
  │  ResetAll                every resource        │
  │  BeginScenarioAll        DI scope per host     │
  │  ── the scenario's steps ──                    │
  │  EndScenarioAll          scopes disposed       │
  └────────────────────────────────────────────────┘
  ↓
Global TearDown             reverse registration order
  ↓
DisposeAsync                reverse registration order
```

Retries repeat the whole per-scenario bracket, not just the steps — a second attempt starts from
the same clean state the first one did.

## Starting up

**`StartAll` runs resources in registration order**, which is the lever you have over dependencies:
a database registered before the host that connects to it is up before the host starts.

A failure here is **catastrophic** — exit code 2, no scenario runs, and the message names the
resource:

```
Resource 'AlbaHost' failed to start: …
```

Resources after the failure are never asked to start. The ones before it are up, and the one that
threw may be half up — both are still disposed, because a resource is recorded as *attempted*
before `Start` is called rather than after it succeeds.

If the failure is a port collision, the message names the process holding the port. That is
deliberate: "failed to start" over somebody else's orphaned container reads as a product regression,
and costs minutes that one `lsof` would have cost nobody.

## Preflight — failing in seconds instead of thousands of times {#preflight}

Preflight validates the environment once, before anything runs, so a broken environment aborts
immediately instead of producing thousands of identical failures.

```csharp
runner.Preflight.Add("the licence file is present", () =>
{
    if (!File.Exists(path)) throw new Exception($"No licence at {path}");
});
```

The contract is **throw to fail** — a check that returns has passed. Every check runs even after
one fails, because the point of a preflight is to tell you everything that is wrong in one go.

Any resource implementing `Check(CancellationToken)` is added automatically, so
`DockerComposeResource` contributes its readiness check without you wiring anything.

There is deliberately **no Bobcat `IEnvironmentCheck`**. JasperFx already owns this concept and
collects checks from `IStatefulResource.Check`, `ISystemPart.AssertEnvironmentAsync` and
Microsoft's `IHealthCheck` — inventing a parallel interface would mean Critter Stack users writing
their checks twice.

A preflight failure is catastrophic: exit code 2, and no feature runs.

## Global actions — once for the whole run {#global-actions}

`IGlobalAction` is cross-cutting setup and teardown that runs **once per run**: seeding reference
data, priming a cache, installing a fake clock.

```csharp
runner.Suite.AddGlobalAction(new SeedReferenceData());
```

`SetUp` runs after every resource has started, so resources are available to it. `TearDown` runs
after the last feature and before resources are disposed, in **reverse** registration order.

A `SetUp` failure is catastrophic — nothing downstream can be trusted. `TearDown` is different:
every action gets its turn even if an earlier one threw, and the failures surface together
afterwards, so one broken teardown cannot hide the rest.

> **If the work owns something — a connection, a container, a host — write a
> [resource](resources.md) instead.** It already has the lifecycle. A global action is for work
> that has no lifecycle of its own.

## Per scenario

Three things happen before your first step, in this order:

1. **`ResetAll`** — every resource's `ResetBetweenScenarios`, in registration order.
2. **`BeginScenarioAll`** — a fresh DI scope on every host resource.
3. Your `BeforeEach`, then the steps.

**Reset comes before the scope, and the order is the point**: persistent state is cleaned first,
then a fresh scope is opened over it. A scope opened first would be holding services bound to data
the reset is about to delete.

Afterwards, `EndScenarioAll` disposes those scopes in reverse registration order.

**A resource with no reset hook carries its state into the next scenario.** That is the single most
common cause of a suite that passes once against a clean database and then reports conflicts for
records it believes are new. See [Resources](resources.md#resetting-between-scenarios).

## Shutting down

`DisposeAsync` runs in reverse registration order, and only over resources that `StartAll`
actually attempted. A resource that was never asked to start is not touched — its `DisposeAsync`
was written assuming `Start` ran, and a second exception from tearing down something that never
came up would only bury the one that matters.

Every resource gets its turn even if an earlier one throws; failures surface together as an
`AggregateException`.

## What this costs you, and where

Two consequences worth budgeting for:

- **Execution always pays the full resource start-up, however few scenarios you selected.** Running
  one scenario from a test explorer still runs `StartAll` for every registered resource. A
  scenario's meaning includes the resources it runs against, and Bobcat will not guess which ones a
  subset needs. Keeping `Start()` fast — reusing running containers, `docker compose up -d` out of
  band — is the lever that matters.
- **The first tracked act of a run pays code generation** on frameworks that compile on first use.
  `Bobcat.Wolverine` primes the compiler outside the tracked window for you; see
  [Bobcat with Wolverine](integrations/wolverine.md).

When you are iterating rather than running once,
[`interactive`](integrating-gherkin.md#interactive) keeps resources warm between runs — `StartAll`
once, the full per-scenario bracket every time.

## See also

- [Resources](resources.md) — the four verbs, hosts, and Docker
- [Integrating Bobcat Gherkin](integrating-gherkin.md) — making the project executable
- [Bobcat with Alba](integrations/alba.md) — the host resource most suites start from
