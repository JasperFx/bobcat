# The Run Lifecycle

Everything Bobcat does to your application happens on a fixed schedule. Knowing it answers most
"why did my suite do that" questions directly — why a scenario saw the previous one's data, why the
first scenario of a run is slower than the rest, why a teardown failure buried the error you
actually wanted.

## The whole run, in order

```
StartAll                    everything registered, in registration order
  ↓
Preflight                   all checks run, results gathered
  ↓
  ┌─ per scenario ─────────────────────────────────┐
  │  ResetAll                every resource        │
  │  BeginScenarioAll        DI scope per host     │
  │  ── the scenario's steps ──                    │
  │  EndScenarioAll          scopes disposed       │
  └────────────────────────────────────────────────┘
  ↓
StopAsync                   reverse registration order
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
threw may be half up — both are still torn down, because a resource is recorded as *attempted*
before `StartAsync` is called rather than after it succeeds.

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

## Once for the whole run {#global-actions}

Cross-cutting setup and teardown — seeding reference data, priming a cache, installing a fake
clock — is a plain **`IHostedService`**, registered in the same list as everything else:

```csharp
runner.Resources.Add(new SeedReferenceData());
```

There is no separate `IGlobalAction` interface and no separate phase. `StartAsync` runs in
registration order alongside the resources, `StopAsync` in reverse, and the ordering lever is
where you register it.

**That ordering is the thing to know if you are moving one across.** Under the old
`IGlobalAction`, every resource started before every global action, whatever order you registered
them in. Now a service registered *before* a resource starts *before* it — so a seeding service
belongs after the database resource it writes to, which is also how it reads.

A start failure is catastrophic — nothing downstream can be trusted. Teardown is different: every
registration gets its turn even if an earlier one threw, and the failures surface together
afterwards, so one broken teardown cannot hide the rest.

> **If the work owns something — a connection, a container, a host — write an
> [`ITestResource`](resources.md) instead.** It is an `IHostedService` too, and it adds the name,
> the between-scenario reset and the preflight check that a bare hosted service has no notion of.

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

`StopAsync` runs in reverse registration order, and only over resources that `StartAll` actually
attempted. A resource that was never asked to start is not touched — its teardown was written
assuming `StartAsync` ran, and a second exception from tearing down something that never came up
would only bury the one that matters.

The suite disposes each resource, and `ITestResource`'s default `DisposeAsync` routes to
`StopAsync`. A resource that writes its **own** `DisposeAsync` must call `StopAsync` from it, or
its teardown never runs here. See
[Teardown is `StopAsync`](resources.md#teardown-is-stopasync-not-disposeasync).

Every resource gets its turn even if an earlier one throws; failures surface together as an
`AggregateException`.

## What this costs you, and where

Two consequences worth budgeting for:

- **Execution always pays the full resource start-up, however few scenarios you selected.** Running
  one scenario from a test explorer still runs `StartAll` for every registered resource. A
  scenario's meaning includes the resources it runs against, and Bobcat will not guess which ones a
  subset needs. Keeping `StartAsync()` fast — reusing running containers, `docker compose up -d` out of
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
