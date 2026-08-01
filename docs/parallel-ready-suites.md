# Making a test suite safe to run in parallel worker processes

What a .NET integration suite has to satisfy before Bobcat's supervisor can split it across worker
processes, and the failure modes to expect on the way. Written from four real suites — Wolverine's
`PersistenceTests` and `Redis.Tests`, Polecat's `Polecat.Tests`, and Bobcat's own — so the list is
what actually broke, not what might.

## Measured so far

| Suite | Tests | Sequential | 4 workers | Result |
|---|---|---|---|---|
| Polecat.Tests | 1587 | 954s | **366s (2.6x)** | 1587/1587 after two fixes |
| Wolverine PersistenceTests | 78 | 164s | **73s (2.2x)** | 78/78, no source changes |
| Wolverine Redis.Tests | 144 | 452s | **206s (2.2x)** | 144/144, no source changes |

All three plateau quickly — 8 workers bought 4% over 4 on Polecat and 3% on Redis — but **for two
different reasons, and the difference decides whether the plateau is worth attacking.**

On Polecat it is contention: beyond a handful of lanes the database container is the bottleneck
rather than the worker count. More hardware would move it.

On Redis it is arithmetic. Partitioning is by class, so a class cannot be split, so **the largest
class is a floor no fleet size goes below**:

```
ceiling = sum(all test durations) / largest class's total duration
```

Redis is 599s of test time with a 188s compliance class in it, so the ceiling is 3.19x and the floor
is ~188s. Measured: 199s at 8 workers. **The prediction cost one run; the curve that confirmed it
cost four.** Worth computing before provisioning a fleet — if the ceiling is 3x, asking for 8 workers
buys nothing but containers.

When the floor is what binds, the fix is not more workers. It is splitting that class, or making the
tests in it faster: two compliance classes were 61% of the Redis suite's total test time.

## Step 0 — be an MTP host

The supervisor drives the test *executable* over Microsoft.Testing.Platform's server mode. It never
loads the test assembly, so **a test project needs no reference to Bobcat**. It does need to be an
MTP host rather than an xUnit native runner:

```xml
<OutputType>Exe</OutputType>
<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
```

Check with `./YourTests --list-tests`. If that errors with `unknown option`, you have the xUnit
native runner and the supervisor cannot drive it.

`UseMicrosoftTestingPlatformRunner` changes only the executable's entry point — the VSTest adapter
path is untouched, so `dotnet test`, `--filter`, TRX and coverlet keep working. Verified on Wolverine,
which runs xUnit v3 in VSTest compatibility mode: `dotnet test --filter "Category!=Flaky"` still
reported 160 passed with the property set.

> **Hazard:** same output path, different entry point. A build without the property silently yields a
> non-supervisable executable. Put it in `Directory.Build.props`, not a per-job `-p:` override.

> **Hazard (found on CritterWatch):** a test project with
> `<GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>` breaks the xUnit v3
> VSTest adapter — the assembly reports `UnknownTargetFramework`, the adapter's process launcher
> cannot decide how to start the (now-executable) project, and every `dotnet test` run dies with
> `Catastrophic failure: … Could not launch test process`. The exe itself still answers
> `--list-tests` and `-automated`, which makes the symptom look like an adapter bug rather than a
> project-property one. Strip the property from test projects when migrating to v3; suites carry it
> for assembly-version-reporting reasons that never apply to a test host.

## Step 1 — the partitioning contract

The supervisor splits work **by test class**, never by individual test. That is a correctness rule,
not a tuning choice: every framework's isolation contract is per class or collection, so a class's
fixtures and static state assume one process.

Splitting Wolverine's suite per *test* failed 1–4 tests non-deterministically; per *class* it was
78/78 at the same wall clock. The mechanism was a class whose setup read

```csharp
var schemaName = "sqlserver" + ++count;   // static int
```

Split across four processes, each restarts at zero and all four collide. **Nothing about that is
visible in the test list**, which is why the rule has to be conservative.

You inherit this for free. The thing you must supply is what follows.

## Step 2 — per-worker resource isolation

Class-level partitioning keeps a class together. It cannot stop two *different* classes that share a
database landing in different workers. So each worker needs its own database:

```csharp
new MtpWorkerFactory(path)
{
    EnvironmentFor = worker => new Dictionary<string, string>
    {
        ["POLECAT_TESTING_DATABASE"] = connectionStringFor($"polecat_w{worker.Lane}")
    }
}
```

`Lane` is bounded by `MaxParallelWorkers`, so you provision as many databases as workers you asked
for. That requires the suite to take its connection string from an environment variable — most
already do; Wolverine's `Servers.cs` was hardcoded constants and needed a small change.

**A suite that starts its own container per process already has this, for free.** Wolverine's Redis
tests spin up a Testcontainers Redis from a `[ModuleInitializer]`, so every worker gets its own
broker with its own port and there is nothing to wire — which is why that suite parallelised with no
source changes at all. Check for this before building per-worker plumbing.

Two things it costs, both worth knowing rather than discovering:

- **Every process pays the container start**, before `Main`, on the critical path — ~0.85s for
  `redis:7-alpine` including discovery, and far more for heavier images. Irrelevant at four workers;
  it dominates any mode that starts one process per test.
- **Nothing reaps those containers if the reaper is off.** A `[ModuleInitializer]` that never
  disposes leaks one container per process; with Testcontainers' Ryuk disabled this reached 49
  orphans on one machine. Not Bobcat's bug, but Bobcat's process multiplication is what makes it
  visible.

## Step 3 — the three bugs this exposes

All were found in real suites, all silent, and all are the reason a first parallel run goes red.

### Text-rewriting a connection string

```csharp
// WRONG — only rewrites while the catalog happens to be "master"
ConnectionSource.ConnectionString.Replace("Initial Catalog=master", $"Database={name}")
```

Point the environment variable at anything else and the literal is absent, the replace matches
nothing, and the "other" database quietly resolves to the **current** one. The test then asserts
against itself and passes or fails for reasons unrelated to what it tests. Use a
`DbConnectionStringBuilder`.

The same shape appears as assertions that bake the environment into an expected value:

```csharp
var expected = new Uri("wolverinedb://postgresql/localhost/postgres/wolverine");
```

That one is honest about what it is — it just cannot survive a renamed database.

### Server-scoped names

**A database a test creates is a sibling of the worker's database, not a child of it.** Per-worker
catalogs therefore do *not* isolate them, and a hardcoded name is one object shared by every worker:

```csharp
// WRONG — every worker races to create and drop the same database
private const string DbA = "polecat_tenant_a";

// RIGHT — carries the worker's scope
private static readonly string DbA = ConnectionSource.Scoped("tenant_a");
```

Under four workers this surfaced as *"User does not have permission to alter database
'polecat_tenant_a'"* — non-deterministically, which is the signature of contention rather than a
deterministic bug.

Applies to anything at server scope: databases, logins, linked servers, Agent jobs, and any fixed
TCP port a test binds.

**Schema names do not need this.** They live inside the catalog, so per-worker databases already
separate them. Polecat has `SchemaName = "doc_usage"` in nine files and it is fine.

### Environment-dependent test identities

A theory whose data is computed from the environment gives the same test a **different identity in
every worker**:

```csharp
// WRONG — the argument (and therefore the test's uid) depends on which worker computes it
public static IEnumerable<object[]> AgentUris =>
    [[$"wolverinedb://postgresql/localhost/{Servers.PostgresDatabaseName}/wolverine"], ...];

// RIGHT — a stable sentinel, resolved inside the test body
[InlineData("default")]
public async Task build_each_agent_smoke_test(string uriString)
{
    var uri = uriString == "default"
        ? new Uri($"wolverinedb://.../{Servers.PostgresDatabaseName}/wolverine") : uriString.ToUri();
```

The supervisor discovers in one process and executes in another with a different per-worker
environment, so the uid the plan asks for does not exist in the worker that receives it. Found on
Wolverine's `MartenTests`: the symptom is *"the worker finished without reporting a result for
this test"* — reported as indeterminate, and a retry appears to "fix" it whenever the retry
process happens to compute the same environment discovery did, which makes it masquerade as an
ordinary flake. Test identity must be a function of the code alone, never of the environment.

## Step 4 — containers

`DockerComposeResource` starts the containers and knows when they are usable. Readiness comes from
the compose file's own `healthcheck`, not from a Bobcat package per image — the healthcheck lives
where the credentials and ports already are.

Four descending tiers, and the one used is reported in `ReadinessSource` so a weak check is never
silent: a supplied `Probe`, then the declared healthcheck, then a TCP connect, then "the container is
running" (which only means the entrypoint has not exited).

`Recycle()` uses `--force-recreate` rather than `restart`, because recycling means throwing the thing
away.

## Triage order for a red first run

1. **Deterministic and database-name-shaped** — text-rewritten connection strings, or assertions
   naming the database. Fix the suite; these are latent bugs regardless of parallelism.
2. **Non-deterministic, moves between runs** — server-scoped name collisions. Scope the names.
3. **Non-deterministic, same class every time** — a class split across workers. Report it: the
   partitioner should have prevented this.

A fourth shape is worth ruling out *before* the first parallel run rather than triaging after it:
a test that only ever passed because something else ran first. Running each test alone and diffing
against the full-suite result finds those, and it is the one class of failure that parallelism
exposes without having caused. See issue #61.

## What this does not buy you

Parallelism speeds up a suite; it does not make a flaky one reliable. If stability is the problem,
that is the retry/quarantine half of the supervisor (`RetryBudget`, `@retry`, `@isolated`,
`RecoveryHint`), and it is a separate decision — Polecat needed none of it.

Nor does it fix a suite dominated by one slow test. Wall clock is the slowest lane, so the largest
partition sets the floor: Wolverine's run bottomed out at ~67s because one test slept for 61 of them.
See issue #56.

And it does not shorten a CI run whose wall clock is set by a *different* job. Wolverine's matrix
runs ~30 jobs concurrently, so the workflow takes as long as its slowest; parallelising the Redis
job from 9m to 5m moved that number by zero, because two 18m jobs set it. Measure which suite is on
the critical path before optimising the one in front of you.
