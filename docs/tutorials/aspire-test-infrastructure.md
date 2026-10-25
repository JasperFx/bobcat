# Test Infrastructure with Aspire

**The problem:** your specifications need real infrastructure -- a Postgres database, a RabbitMQ
broker, a SQL Server -- and something has to start it before the suite runs. The usual answer is a
`docker-compose.yml` beside the solution and a `docker compose up -d` that everybody (and every CI
job) has to remember, with fixed ports that collide with whatever else is running on the machine.

**What you will build:** an [Aspire](https://aspire.dev) AppHost that *owns* the test
infrastructure, and a Bobcat spec assembly that starts it **once**, waits until it is healthy, reads
its connection strings and hands them to the system under test. `dotnet test` is the whole
instruction: no compose file, no separate step, no fixed ports.

The code on this page is the compiled sample pair in the Bobcat repository,
[`src/Bobcat.Aspire.Samples.AppHost`](https://github.com/JasperFx/bobcat/tree/main/src/Bobcat.Aspire.Samples.AppHost)
and [`src/Bobcat.Aspire.Samples`](https://github.com/JasperFx/bobcat/tree/main/src/Bobcat.Aspire.Samples),
built against Aspire 13.6 and xUnit v3.

## Step 1 -- an AppHost that owns the infrastructure

An AppHost is a small executable project on the Aspire SDK. It plays the part `docker-compose.yml`
used to play, written in C#:

```xml
<Project Sdk="Aspire.AppHost.Sdk/13.6.3">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.PostgreSQL" Version="13.6.3" />
  </ItemGroup>
</Project>
```

<<< ../../src/Bobcat.Aspire.Samples.AppHost/AppHost.cs#apphost{cs}

Where a compose file names an image, a port and a password, this names a **resource**. Aspire picks
the image, maps the container to a free host port, generates the password, attaches a health check,
and can tell any consumer the whole connection string.

Other infrastructure is one line each (with `Aspire.Hosting.RabbitMQ` and
`Aspire.Hosting.SqlServer`):

<<< ../../src/Bobcat.Aspire.Samples.AppHost/MoreInfrastructure.cs#more-resources{cs}

The AppHost is not your application's AppHost, though it can be. A suite that only needs
infrastructure is better served by one of its own, because the suite then starts exactly what it
uses.

## Step 2 -- start it once, from the spec assembly

The spec project references the AppHost like any other project, plus `Aspire.Hosting.Testing`:

```xml
<ItemGroup>
  <PackageReference Include="Aspire.Hosting.Testing" Version="13.6.3" />
  <PackageReference Include="Npgsql" Version="10.0.0" />
</ItemGroup>

<ItemGroup>
  <ProjectReference Include="..\Bobcat.Aspire.Samples.AppHost\Bobcat.Aspire.Samples.AppHost.csproj" />
</ItemGroup>
```

The reference is what makes Aspire generate `Projects.Bobcat_Aspire_Samples_AppHost`, the type
`DistributedApplicationTestingBuilder` starts. Start it in an **xUnit v3 assembly fixture**, so it
runs once per test process however many spec classes use it:

<<< ../../src/Bobcat.Aspire.Samples/TestInfrastructure.cs#assembly-fixture-registration{cs}

<<< ../../src/Bobcat.Aspire.Samples/TestInfrastructure.cs#test-infrastructure{cs}

What each part is for:

- **`CreateAsync<Projects.X>()` → `BuildAsync` → `StartAsync`** runs the AppHost in-process, under
  the test runner, and starts its containers.
- **`WaitForResourceHealthyAsync("orders")`** is the difference between *started* and *ready*. A
  Postgres container is running well before it accepts connections, and before the `orders` database
  exists; the first spec would otherwise race it.
- **`GetConnectionStringAsync("orders")`** returns a whole connection string -- host, the port Aspire
  chose, credentials, database. Hand it on as it is; never stitch a database name or port onto a base
  string.
- **The logging filters** keep each container's own output and the health-check retries during boot
  out of your test output. Take them out when a resource will not start and you need to see why.
- **`ConnectionStrings__orders`** is the Aspire configuration convention for the same string. When
  something has already been handed one -- a supervised worker with a database of its own (below) --
  the fixture uses it and starts nothing.
- **`DisposeAsync`** stops the containers when the test process finishes.

### A startup failure is catastrophic

If the AppHost cannot start -- Docker is not running, an image cannot be pulled, a resource never
becomes healthy -- nothing in the assembly can pass. Say so with a **`SpecCatastrophicException`**:
Bobcat halts every scenario that opens afterwards with that one cause
([Environment lost](../marker-steps.md#environment-lost)), never retries it even under a `Retry`
trait, and a [supervised run aborts](../parallel-ready-suites.md#a-speccatastrophicexception-stops-a-supervised-run)
with exit code 2 instead of scheduling the rest. Captured from the sample with Docker unreachable:

```text
failed Bobcat.Aspire.Samples.OrderLedgerSpecs.a_customer_total_sums_their_orders (0ms)
  Xunit.MicrosoftTestingPlatform.XunitException: Assembly fixture type 'Bobcat.Aspire.Samples.TestInfrastructure' threw in InitializeAsync
  ---- Bobcat.Engine.SpecCatastrophicException : The Aspire test infrastructure did not start (is Docker running?): Container runtime 'docker' was found but appears to be unhealthy. Ensure that Docker is running and that the Docker daemon is accessible. If Resource Saver mode is enabled, containers may not run.
  -------- Aspire.Hosting.DistributedApplicationException : Container runtime 'docker' was found but appears to be unhealthy. ...
```

One cause, stated once per test, rather than a connection-refused stack trace from inside each spec.
The timeout matters too: without one, a resource that never becomes healthy hangs the run.

## Step 3 -- specifications against it

The fixture is a constructor parameter. The system under test -- here a small Postgres-backed
`OrderLedger` that knows nothing about Aspire or Bobcat -- is built from the connection string, and
the specs are ordinary `[BobcatSpec]` tests over `[Given]`/`[Then]` step helpers:

<<< ../../src/Bobcat.Aspire.Samples/OrderLedgerSpecs.cs#specs{cs}

For an application host (Alba, `WebApplicationFactory`, `Host.CreateApplicationBuilder`), hand the
same string over as configuration, under the name the application already reads -- for example
`ConnectionStrings:orders` -- so the application starts the way it does in production.

Run against a real Docker daemon, with `BOBCAT_SPEC_CONSOLE=1` to print the specifications:

```text
Feature: Order ledger
══════════════════════

  a customer total sums their orders OK
  ────────────────────────────────────────────
    ✓ Given the ledger is empty (12ms)
    ✓ And   Ada places an order for 40 (4ms)
    ✓ And   Ada places an order for 2.50 (1ms)
    ✓ Then  Ada has spent 42.50 (4ms)

  Succeeded with Rights: 4, Wrongs: 0, Errors: 0
  Duration: 27ms (steps 21ms)


  another customer orders are not counted OK
  ─────────────────────────────────────────────────
    ✓ Given the ledger is empty (8ms)
    ✓ And   Ada places an order for 10 (1ms)
    ✓ And   Grace places an order for 99
    ✓ Then  Ada has spent 10 (1ms)

  Succeeded with Rights: 4, Wrongs: 0, Errors: 0
  Duration: 11ms (steps 10ms)

2 specification(s) — all green
```

The whole run took about 13 seconds on a laptop with the Postgres image already pulled, almost all of
it the container starting -- once, not per spec. Note that "lifecycle not measured" in Bobcat's
timing summary is that startup: fixture setup belongs to the test runner, not to any scenario.

Both specs reset the same table, which is safe because xUnit runs the tests of one class one at a
time. Classes run in parallel by default, so a second class against the same database needs its own
tables or schema, or a shared `[Collection]`.

## Aspire or docker-compose?

| | docker-compose | Aspire AppHost |
|---|---|---|
| Starting it | a separate `docker compose up -d` before `dotnet test` | the suite starts it; `dotnet test` is the whole instruction |
| Ports | fixed, and they collide with what is already running | chosen per run, so they never collide |
| Ready, not just running | `--wait` plus a hand-written healthcheck | `WaitForResourceHealthyAsync`, health checks built in |
| Connection strings | written by hand to match the compose file | `GetConnectionStringAsync`, always whole |
| Lifetime | outlives the run; shared by every run until you stop it | one set of containers per test process, stopped at the end |
| Docker | required | **still required** -- Aspire orchestrates containers, it does not replace the runtime |

The trade-offs worth knowing before switching:

- **Docker is still a prerequisite**, locally and on CI. GitHub's Linux runners have it; its macOS and
  Windows runners cannot run Linux containers. What goes away is the separate start step and the
  workflow's `services:` block.
- **Startup is paid per test process.** A compose stack started once serves every run that follows;
  an AppHost starts fresh containers each run. That is also the point -- every run starts empty --
  but on a large suite run many times a day, a long-lived database may be the cheaper choice.
- **The suite is the only consumer.** Nothing else on the machine can reach these containers by a
  known port, which is what keeps runs from colliding and also means you cannot point a SQL client at
  "the test database" between runs.
- **Recycling is a compose feature.** The supervisor's
  [`DockerComposeResource`](../parallel-ready-suites.md#step-4-containers) is what a
  `[ClearsOnRecycle]` hint or `RecycleOnRetry` trait recycles; Bobcat ships no Aspire equivalent, so
  a suite that recycles a broker between retries keeps that broker in compose.

## Parallel lanes and per-worker databases

Under the [Supervisor](../parallel-ready-suites.md), each lane is its own worker **process**, so each
lane runs the assembly fixture and starts its **own** AppHost and its own Postgres container. That is
per-worker isolation with no further work -- no shared database for two classes in different workers
to fight over -- at the cost of one set of containers per lane, each booting before its lane can start.

When that cost is too high, start the infrastructure once outside the run (compose, or a long-running
AppHost) and give each worker a database of its own with
[`LaneDatabases`](../parallel-ready-suites.md#step-2-per-worker-resource-isolation). Each worker is
launched with `ConnectionStrings__orders` set to its lane's database, and the fixture above takes that
string and starts nothing -- the same suite, run three ways: with Aspire, under the supervisor, or by
hand with the variable set.

## Other test frameworks

The fixture is the only xUnit-specific part. Start the AppHost once wherever your framework runs
once-per-assembly setup -- a TUnit `[Before(TestSession)]` hook or a
`[ClassDataSource<T>(Shared = SharedType.PerTestSession)]`, NUnit's `[SetUpFixture]`, MSTest's
`[AssemblyInitialize]` -- and throw `SpecCatastrophicException` from it the same way. Bobcat halts the
run on it from setup in all four.

## Running the sample

The sample is compiled by every Bobcat build so this page cannot drift from working code, but it is
**not** run by `dotnet test` or `./build.sh CI` there: running it starts a container, and the
repository's own build must not need Docker. With Docker running:

```bash
dotnet build src/Bobcat.Aspire.Samples
./src/Bobcat.Aspire.Samples/bin/Debug/net10.0/Bobcat.Aspire.Samples
```

In your own repository the spec project is an ordinary test project, and `dotnet test` runs it.
