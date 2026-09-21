# Resources

A **resource** is anything with a lifecycle that your specs run against — an application host, a
database, a set of containers, a broker. Bobcat starts them, resets them between scenarios, hands
them to your steps, and disposes them, on the schedule in [The Run Lifecycle](run-lifecycle.md).

```csharp
[BobcatConfiguration]
public static void Configure(BobcatRunner runner)
{
    runner.Resources.Add(new AlbaResource<Program>());
}
```

Bobcat ships `HostResource` and `DockerComposeResource`; a host you drive through a client of your
own — Alba, say — is a resource you write, and that is a handful of members. This page is for
understanding what a resource does, and for writing one.

## Reaching a resource from a step

```csharp
var host = Context!.GetResource<IHostResource>().Host;
```

Every resource has a `Name`, and every lookup takes an optional one. With no name, a lookup finds
the single resource of that type — and **throws if there are two**, rather than picking one. That
pair is how a suite drives more than one host or more than one store at once:

```csharp
runner.Resources.Add("orders", new AlbaResource<OrdersProgram>());
runner.Resources.Add("billing", new AlbaResource<BillingProgram>());

var orders = Context!.GetResource<IHostResource>("orders");
```

## The five verbs

`ITestResource` **is an `IHostedService`**, so its two lifecycle verbs are the ones you already
know — `StartAsync` and `StopAsync`. Bobcat adds `ResetBetweenScenarios`, and two sibling
interfaces add a verb each. They are deliberately five different things, not one with flags.

| verb | who calls it | what it assumes |
|---|---|---|
| **`StartAsync`** | the suite, once, in registration order | nothing is up yet |
| **`ResetBetweenScenarios`** | the suite, before every scenario | the resource is up and healthy; its *state* is dirty |
| **`StopAsync`** | the suite, once, in reverse order | the run is over |
| **`Recycle`** (`IRecyclableResource`) | the **supervisor**, between attempts, outside any scenario | the resource is **broken** |
| **`Restart`** (`IRestartableResource`) | a **step**, mid-scenario, because the spec says so | the resource is **healthy** |

The last two look alike and are not. Conflating them would let the supervisor "recycle" an
in-process host it cannot see, and would make a spec's restart step read as a recovery action.

There is also `Check(CancellationToken)`, a no-op by default, which contributes the resource to
[preflight](run-lifecycle.md#preflight).

### Teardown is `StopAsync`, not `DisposeAsync`

`ITestResource` still extends `IAsyncDisposable`, but its **default `DisposeAsync` delegates to
`StopAsync`** — so a resource that owns nothing beyond what `StopAsync` releases does not write a
disposer at all, and `await using` over it still tears it down.

Write your own `DisposeAsync` only for handles `StopAsync` leaves open, and **call `StopAsync`
from it**: the suite disposes, so a `DisposeAsync` that does not reach `StopAsync` means your
teardown never runs.

## Resetting between scenarios

This is the one you have to get right, and the only one with no safe default.

**A resource with no reset hook carries its state into the next scenario.** A suite that passes
once against a clean database and then reports conflicts for records it believes are new is not
unlucky — it is the default outcome for anything with a unique index.

```csharp
new AlbaResource<Program>(reset: async host =>
{
    var store = host.Services.GetRequiredService<IDocumentStore>();
    await store.Advanced.Clean.DeleteAllDocumentsAsync();
});
```

Three things reset does **not** cover, each of which has bitten a real suite:

- **Seed data written by your own `Program.cs`.** It is tempting to assume that code never runs
  under a test host, on the theory that the factory intercepts `Build()` and stops there. It does
  not — the seed is in the database before the first scenario begins. Your reset removes it, so a
  spec must not count on seeded rows either. Decide which you want and say so where the resource is
  registered.
- **A message broker.** Resetting the store deletes the outbox row while the queued message is
  still sitting on RabbitMQ, ready to be delivered into the *next* scenario and fail an assertion
  that has nothing to do with it. See `DrainTransportsAsync` in
  [Bobcat with Wolverine](integrations/wolverine.md).
- **Containers.** `DockerComposeResource.ResetBetweenScenarios` is a deliberate no-op — containers
  are recycled or left alone, never reset per scenario.

A **restart is not a reset**: `ResetBetweenScenarios` does not run across one, because the point of
a restart is usually to prove that persistent state *survives*.

## Host resources and `IHost`

`IHostResource` is a resource that owns an `IHost` — `HostResource<TProgram>` for a generic host,
`AlbaResource<TProgram>` when you also want to drive it over HTTP.

```csharp
public interface IHostResource : ITestResource
{
    IHost Host { get; }
    IServiceProvider RootServices { get; }      // the application's root container
    IServiceProvider CurrentServices { get; }   // this scenario's scope
}
```

### The scenario scope

Every host resource gets a **fresh DI scope per scenario**, opened after `ResetAll` and disposed
after the scenario ends. `CurrentServices` is that scope; `RootServices` is the application's root
container.

Resolve scoped services from `CurrentServices` unless you specifically want the root — a scoped
service resolved from the root lives for the whole run and is shared by every scenario, which is
the same class of problem as forgetting a reset hook.

### Restarting a host mid-scenario

`HostResource` and `AlbaResource` implement `IRestartableResource`, for specs whose subject *is*
the restart — "the application restarts and forgets nothing", "a queued message survives a bounce":

```csharp
[When("the application restarts")]
public Task Restarts() => Context!.GetResource<IHostResource>() is IRestartableResource r
    ? r.Restart()
    : throw new InvalidOperationException("not restartable");
```

The resource keeps its name across a restart, and the scenario's scope follows the new container:
if a scope was open on the old host, it is closed, the host restarts, and a fresh scope opens on
the new one.

> **Anything a step captured from the old scope is dead after a restart. Re-resolve, don't hoard.**

### Use an explicit `Main`, not top-level statements

If your spec project uses top-level statements **and** project-references the host (which also has
a `Program`), both compilations synthesize a `Program` in the global namespace.
`AlbaResource<Program>` then binds to the test runner's stub and bootstraps an empty
`WebApplication`, which crashes natively in `WebApplication.CreateBuilder` — a `PAL_SEHException`
with no managed stack.

```csharp
public static class SpecsRunner
{
    public static Task<int> Main(string[] args) => …;
}
```

Bobcat detects two global-namespace `Program` types and throws a `BobcatConfigurationException`
pointing here, before Alba can crash natively. It also presents as a target-framework mismatch, so
check this first when `AlbaResource<Program>` behaves as though it bound to nothing.

### Hosts that run JasperFx commands

Every Critter Stack `Program.Main` ends in `return await app.RunJasperFxCommands(args);`. Under a
test host that `Main` runs with the factory's synthesized arguments, and JasperFx parses a command
line never meant for it.

`JasperFxEnvironment.AutoStartHost = true` is JasperFx's own switch for exactly this, and
**`AlbaResource` sets it for you** on `StartAsync()`. What you will still see on the console is
JasperFx-side and harmless — an assembly scan line, and a note that it ignored the factory's
`--environment` flag.

If a `RunJasperFxCommands` host fails to *start* under a test host, that flag is the first thing to
check.

### Quieting a noisy host

An ASP.NET Core host at its default `Information` level writes several lines per request, which
buries the run summary. `AlbaResource<TProgram>` puts a floor under the hosted application's
**console** logging — `ConsoleLogLevel`, default `Warning`, fluent `WithConsoleLogLevel(level)`,
`null` to leave the application's logging exactly as it ships.

It is a filter rule scoped to the console provider rather than `SetMinimumLevel`, because an
`appsettings.json` `"Logging:LogLevel:Default": "Information"` is itself a rule and rules beat the
minimum level. Every other sink is left alone.

## Docker resources

`DockerComposeResource` runs containers from a `docker-compose.yml` and can recycle them during a
run.

```csharp
runner.Resources.Add(new DockerComposeResource("infrastructure")
{
    Services = ["postgres", "rabbitmq"],
    ReadyWhenListeningOn = 5432
}.UsingComposeFile("docker-compose.yml"));
```

| | |
|---|---|
| `UsingComposeFile(path)` | Compose files in `-f` order. Defaults to compose's own discovery |
| `Services` | Which services to manage. Empty means every service in the file |
| `WorkingDirectory` | Where `docker compose` runs. Defaults to the current directory |
| `StartTimeout` | How long to wait for readiness. Default 3 minutes |
| `StopOnDispose` | `docker compose down` at the end. **Off by default** |
| `Log` | Progress, for a console or a log |

`StopOnDispose` is off because leaving containers up between runs is what makes the second run
fast, and is what a developer expects locally. CI throws the whole machine away regardless.

### Readiness is declared in the compose file, not in Bobcat

Docker already has a mechanism for "is this container usable yet" — the `healthcheck` block — and
it lives next to the credentials and the port mapping the probe needs:

```yaml
healthcheck:
  test: ["CMD-SHELL", "pg_isready -U postgres"]
  interval: 10s
  start_period: 20s
```

So this class knows nothing about Postgres, SQL Server or RabbitMQ. Readiness is decided in four
descending tiers:

1. **`Probe`**, when you supply one — a real query beats any proxy.
2. **The service's declared Docker healthcheck.**
3. **A TCP connect** to `ReadyWhenListeningOn`, when given.
4. Otherwise **"the container is running"**, which is weak.

**The tier actually used is reported** on `ReadinessSource`, and the weakest one says so out loud:

```
Resource 'infrastructure' is only known to be running — declare a healthcheck in the compose
file, or set ReadyWhenListeningOn/Probe, to actually establish readiness.
```

That exists so a green run never hides the fact that it only ever checked the entrypoint had not
exited. A database *accepting logins* is a different claim, and tier 4 has not tested it.

### Recycling

`Recycle` uses `--force-recreate` rather than `restart`, because recycling means throwing the thing
away. A broker whose in-flight state cannot be drained is exactly the case it exists for, and
restarting the process would keep that state.

Recyclable resources belong to the **supervisor**, not a worker — a worker cannot restart the
broker it is about to be replaced alongside. Register them with
`Supervisor.AddRecyclableResource`.

## Writing your own

Implement `ITestResource` when the thing is named, needs resetting between scenarios, or should
join preflight. When it needs none of those — seeding reference data, installing a fake clock —
register a plain [`IHostedService`](run-lifecycle.md#global-actions) instead; the list takes both.

```csharp
public class SftpServerResource : ITestResource
{
    public string Name => "sftp";

    public Task StartAsync(CancellationToken token = default) => …; // once, in registration order
    public Task ResetBetweenScenarios() => …;                       // before every scenario
    public Task StopAsync(CancellationToken token = default) => …;  // reverse order, only if
                                                                    // StartAsync was attempted
    public Task Check(CancellationToken token) => …;                // optional: joins preflight
}
```

There is no `DisposeAsync` there on purpose — the interface's default routes it to `StopAsync`.

Add `IRecyclableResource` if it can be thrown away and stood up fresh, and `IRestartableResource`
if a *spec* should be able to bounce it mid-scenario.

## See also

- [The Run Lifecycle](run-lifecycle.md) — when each of these is called
- [Bobcat with Alba](integrations/alba.md) · [Marten](integrations/marten.md) · [Wolverine](integrations/wolverine.md)
- [Parallel-Ready Suites](parallel-ready-suites.md) — per-worker resource isolation
