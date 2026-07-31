# Making a test suite safe to run in parallel worker processes

What a .NET integration suite has to satisfy before Bobcat's supervisor can split it across worker
processes, and the failure modes to expect on the way. Written from three real suites — Wolverine's
`PersistenceTests`, Polecat's `Polecat.Tests`, and Bobcat's own — so the list is what actually broke,
not what might.

## Measured so far

| Suite | Tests | Sequential | 4 workers | Result |
|---|---|---|---|---|
| Polecat.Tests | 1587 | 954s | **366s (2.6x)** | 1587/1587 after two fixes |
| Wolverine PersistenceTests | 78 | 164s | **73s (2.2x)** | 78/78, no source changes |

Both plateau quickly: on Polecat, 8 workers bought 4% over 4. Beyond a handful of lanes the database
container is the bottleneck, not the worker count — which is convenient, since CI runners have few
cores anyway.

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

## Step 3 — the two bugs this exposes

Both were found in real suites, both silent, and both are the reason a first parallel run goes red.

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

## What this does not buy you

Parallelism speeds up a suite; it does not make a flaky one reliable. If stability is the problem,
that is the retry/quarantine half of the supervisor (`RetryBudget`, `@retry`, `@isolated`,
`RecoveryHint`), and it is a separate decision — Polecat needed none of it.

Nor does it fix a suite dominated by one slow test. Wall clock is the slowest lane, so the largest
partition sets the floor: Wolverine's run bottomed out at ~67s because one test slept for 61 of them.
See issue #56.
