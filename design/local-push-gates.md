# Design note: local commit-and-push gates across a repository's test projects

**Written 2026-10-18 for [#566](https://github.com/JasperFx/bobcat/issues/566).** The scope was set
by Jeremy on 2026-10-18: in 1.0 this ships as a design note only. Building the gate is
[#729](https://github.com/JasperFx/bobcat/issues/729) (Post-1.0). The input is the second Supervisor
rollout ([#565](https://github.com/JasperFx/bobcat/issues/565), lessons of record in
`critter-stack-supervisor-rollout.md`) and the four repositories' default branches as of
2026-10-17: marten `2ece3a9`, polecat `817a902`, weasel `79827af`, fisher `4504969`.

The question: one command runs **all** of a repository's test projects before a push, as fast as
this machine allows, without two suites colliding on a shared container. Today each repository
runs its suites one Nuke target at a time, and a Supervisor drives one MTP host.

## The four repositories' constraints

What each project needs, and what stops it running beside another. "Isolation" is how the
repository's `build/` already keeps lanes apart (lesson 8). Everything below was read from the
default branches. Where something was not checked, the table says so.

### Marten: one Postgres, one database, and server-scoped literals

Container: `docker-compose.yml` → `postgresql`, a custom image (Postgres 17 + PostGIS + pgvector),
host port **5432**, database `marten_testing`. `src/MultiHostTests` has its own compose file
(primary and replica on 5440/5441) and is outside the `Test` target.

| Projects | Container | What they share |
|---|---|---|
| `Marten.Testing`, Core, DocumentDb, EventSourcing (4 workers), Daemon, ModularConfig, Linq (4 workers), MultiTenancy, TenantPartitionedEvents, Patching, ValueTypes, ContainerScopedProjections, Stress, CompiledQueries, SourceGenerator, AotRuntime (`Test`) | postgresql | Every project reads one variable, `marten_testing_database` (`Marten.Testing/Harness/ConnectionSource.cs`), so they all point at **one database** unless a lane overrides it |
| NodaTime, AspNetCore, PostGIS, PgVector, TimescaleDB, EntityFrameworkCore, MemoryPack (`TestExtensions`) | postgresql (TimescaleDB's extension not checked against the image) | The same harness and database |

What stops two Marten projects running at once on that container:

- **Schema names come from the class name** (`IntegrationContext`: `GetType().Name.Sanitize()`,
  `HostedStoreContext` likewise), and some fixtures use literals (`DefaultStoreFixture`:
  `string_events`, `MartenComplianceFixture`: `compliance`, `compliance_shards`). Two projects in one
  database can collide on a class name, and `DestructiveIntegrationContext` calls
  `CompletelyRemoveAllAsync`.
- **Server-scoped literal databases.** CoreTests (`jasper_fx_mechanics`) and DaemonTests
  (`dynamic_spin_up_of_dynamic_tenants`, `multi_tenancy_by_database`) `CREATE DATABASE` by fixed
  name: `database1`, `tenant1` to `tenant4`. A database per project does not isolate those. This is
  bug shape #2 from `docs/parallel-ready-suites.md`, across projects instead of across lanes.
- Every assembly sets `DisableTestParallelization = true`, so Marten's parallelism is the
  Supervisor's lanes, not xUnit's.
- Lanes use `marten_testing_w{lane}` with no project or framework in the name. Two projects at
  more than one worker, or one project on two TFMs, would share lane databases.

### Polecat: one SQL Server, already scoped

Container: `polecat-sqlserver`, SQL Server 2025, host port **11433**. On an Apple-silicon machine it
runs under x64 emulation.

| Project | Workers | Isolation |
|---|---|---|
| Polecat.Tests | 4 (CI passes `TEST_WORKERS: 4`) | A catalog per lane, `polecat_w{lane}`, recreated every run. Every server-scoped name goes through `ConnectionSource.Scoped()` (`polecat_w3_tenant_a`) |
| Polecat.AspNetCore.Testing, Polecat.EntityFrameworkCore.Tests | 1 | The default catalog, `master`, so their scoped names are `master_*` |

The names are already disjoint: the companions never touch a lane catalog. **Polecat's limit is
the container's capacity, not names.** Locally, 4 workers gave 4 timeouts that never happened on
CI (see Measured in `critter-stack-supervisor-rollout.md`), and lesson 9 could not rule out contention on one SQL Server. Note
also that the local `Test` target still runs plain `dotnet test`. Only the CI targets are supervised.

### Weasel: seven containers, one project that needs three

Containers (`docker-compose.yml`): postgresql **5432** (database `marten_testing`), sqlserver
**1435** (azure-sql-edge), oracle **1521**, mysql **3306**, firebird3/4/5 on **3063/3064/3065**.

| Project | Containers | Workers | Isolation |
|---|---|---|---|
| Weasel.Core.Tests, Weasel.Sqlite.Tests | none | 1 | none needed |
| Weasel.CommandLine.Tests | none found (run by no workflow) | 1 | not checked |
| Weasel.Postgresql.Tests | postgresql | 4 | database per lane, `{configured}_w{lane}` |
| Weasel.SqlServer.Tests | sqlserver | 4 | database per lane, `weasel_w{lane}` |
| Weasel.Firebird.Tests | one firebird | 4 | none needed: a database file per class |
| Weasel.MySql.Tests | mysql | 1 | none possible: literal server-scoped schema names |
| Weasel.Oracle.Tests | oracle | 1 | none possible: literal users in the PDB |
| Weasel.EntityFrameworkCore.Tests | **firebird, postgresql and sqlserver** | 1 | the base connection strings |

The EF Core suite uses the same variables as the per-engine suites
(`weasel_{firebird,postgresql,sqlserver}_testing_database`) but at their **base** databases, while
the per-engine suites at 4 workers use lane databases. Whether the two collide at the same time was
not measured.

### Fisher: no containers

SQLite files under `Path.GetTempPath()`. `build/SupervisedTests.cs` gives every lane its own temp
directory (`.nuke/temp/lanes/{suite}-{tfm}/lane-{n}`), so suites and target frameworks are already
disjoint. Fisher.Tests runs at 2 workers and the two companions at 1. xUnit already runs
Fisher.Tests' collections in parallel in one process. Lesson 13 measured it at 6.6–7.3x in-worker
concurrency, and 1 → 4 workers bought about 10–15%. **Fisher's only constraint is the CPU.**

### Across repositories

**Marten and Weasel both publish Postgres on host port 5432 with database `marten_testing`**. (Wolverine's compose
file publishes its Postgres on 5433, so it does not collide.) Whichever repository's compose
project started first owns the port. Marten needs its own image (PostGIS, pgvector), so Weasel's
`postgres:15.3-alpine` answering on 5432 is not a container Marten's extensions can pass on. A gate
cannot fix that, and should not try (the machine is shared). It can **notice** it before running
anything: see Preflight below.

## What the comparison says

1. **The unit a scheduler reasons about is a resource, not a project.** Weasel's EF Core suite
   needs three containers at once. If each project belonged to exactly one track, that suite would
   need a special case. Instead, a project names the resources it needs, and runs only when all of
   them are free.
2. **One project's lanes already saturate its container.** Marten's EventSourcing and Linq run at
   4 workers. Polecat ran 4 SQL Server hosts and timed out locally. Nothing measured says two projects overlapping on a container
   beat running them one after the other at full lanes. Lesson 9 suggests they might do worse.
3. **Names, not containers, are what make two projects collide.** Polecat's projects are already
   name-disjoint. Marten's are not (one database, server-scoped literals). A scheduler cannot see
   names, so the declaration has to state them, and the conservative default is "shares the
   container exclusively".
4. **The large win is concurrency across containers.** That is Weasel (seven containers, plus four
   suites that need none). Marten and Polecat each have one container that every project needs, so
   a gate there is about one command, one build and one report rather than a faster wall clock.
   That holds unless a second container is added. Fisher is bound by cores.
5. **The CPU is a resource too.** The `ProcessorCount / 2` clamp (lesson 6) was per Supervisor. A
   gate running several Supervisors at once needs one budget across all of them, or four suites
   at "2 workers each" put eight hosts on a four-core machine.

## Proposal: the declaration

**C#, in `build/`, beside the targets that already configure each project.** All four
repositories already have `build/SupervisedTests.cs`, where workers, lane isolation
(`LaneDatabases`, `EnvironmentFor`, `TMPDIR`) and partitioning are set in code. Isolation needs a
`DbProviderFactory` and a delegate, and neither MSBuild items nor traits can carry those. Traits are
also per test, and these are per-project facts. A second, declarative copy of the same facts in
another syntax would drift from the code that acts on them.

```csharp
// build/Gate.cs: the shape, not a final API
var gate = new GateDeclaration
{
    // A track is a named resource a project holds while it runs. Capacity 1 by default.
    Tracks =
    {
        Track.Container("postgres", "localhost:5432", expectImage: "marten-postgres"),
    },
    // CPU budget across every running worker. Default ProcessorCount / 2 (lesson 6).
    WorkerBudget = Environment.ProcessorCount / 2,
};

gate.Project("src/CoreTests", workers: 1).On("postgres");
gate.Project("src/EventSourcingTests", workers: 4).On("postgres");
gate.Project("src/Marten.SourceGenerator.Tests");          // no track: runs whenever the budget allows

// Weasel's EF Core suite holds three tracks at once.
gate.Project("src/Weasel.EntityFrameworkCore.Tests").On("firebird5", "postgres", "sqlserver");
```

The rules:

- **A track is exclusive by default.** A project runs only while it holds every track it names.
  Projects on one track run one at a time, and tracks run concurrently. That is the issue's track
  model, generalised so that one project can hold several.
- **A project with no track** (Weasel Core/Sqlite, every Fisher suite, Marten's source-generator
  tests) is limited only by the worker budget.
- **Each project keeps its Supervisor configuration.** `Project(...)` returns the same settings
  each repository already builds by hand: workers, `EnvironmentFor` and `LaneDatabases`,
  `PartitionKey`, the retry policy. The gate does not invent a second way to configure a
  Supervisor. Moving each repository's existing `runSupervised` into this shape is most of the
  adoption work.
- **Sharing a track is a later, opt-in refinement**, for example `Track(..., capacity: 2)` plus a
  per-project claim that its names are disjoint (Polecat's `Scoped()`, Weasel's lane databases). It
  is not built until a measurement shows two projects on one container beating one after the other
  (point 2). Until then capacity is 1 and the declaration does not offer it.
- **A target framework is part of the unit.** `(project, tfm)` is what gets scheduled. Two TFMs of
  one project hold the same tracks, because Marten's lane databases do not carry the TFM. Fisher's
  per-TFM directories would let it name no track.

## Proposal: the scheduler

**A `Gate` type in `Bobcat.Supervisor` that runs several `Supervisor` instances in one process, and
a Nuke target in each repository that calls it.** One Supervisor driving several hosts is not the
shape: its partitioning, ledger matching, stall handling and `RunReport` are all per host, and they
are right as they are. The gate is a scheduler above them. This depends on the Supervisor having
no process-wide mutable state. The only statics in `src/Bobcat.Supervisor` today are read-only
constants and dialects, and #729 has to keep it that way.

1. **Build once.** One `dotnet build` of the solution for the frameworks asked for, then every host
   runs with `--no-build`. That also pays the Nuke bootstrap once per gate instead of once per
   suite (lesson 10, where 7–11 s of bootstrap made Weasel's tiny suites slower).
2. **Preflight every track before launching anything** (`Supervisor.Preflight`, the JasperFx
   environment checks). A track that names an address checks that something answers there. With
   `expectImage`, or with the compose project from `COMPOSE_PROJECT_NAME`, it checks that the
   answer is this repository's container, which catches the 5432 collision above. A failed
   preflight is reported by track and fails the gate before any test runs. The gate never starts,
   stops or resets a container. That is `./build.sh Docker`'s job.
3. **Predict each unit's duration** from the ledger: the lane plan's makespan, which `WorkPlan`
   already computes from `KnownTestDurations`. Fall back to test count from discovery, and print
   `Known durations matched N/M` per project as #663 does. Locally the ledger is the gate's own,
   written to `.nuke/temp` from previous gate runs. #730 keys it by display name, so CI's artifact
   can seed it too.
4. **Schedule by list scheduling, longest first.** Whenever a unit finishes, start the
   longest-predicted waiting unit whose tracks are all free and whose workers fit in the remaining
   budget. A unit that wants more workers than the budget allows is clamped to the budget, not
   held back forever. Print the plan before running (the tracks, the order, the predicted
   makespan), and print predicted against actual afterwards. That is also lesson 9's "compare a
   plan's predicted and realised makespans".
5. **Report, don't act** beyond scheduling. One line per unit with its verdict and wall clock, then
   each unit's `RunReport.ToText` for anything not clean. The gate's exit code is the worst of its
   units: Indeterminate (2) over failed (1) over pass (0), the Supervisor's own convention. A
   pass-on-retry is reported as flaky, never folded into clean. Nothing is retried that the
   project's own policy would not retry.

### Expected effect per repository

These are predictions from the measured figures, not measurements:

- **Weasel** gains the most. Postgres, SQL Server, Oracle, MySQL and one Firebird run at once.
  Core, Sqlite and CommandLine fill the gaps, and EF Core waits until its three tracks are free.
  The makespan should approach the slowest track (Firebird, 76–117 s on CI), not the sum.
- **Fisher**: its three suites and two frameworks run at once on cores alone. Lesson 13 says the
  cores are already busy, so expect roughly parity with a sequential run. The value is one command.
- **Marten**: every project holds the one Postgres track, so the gate is the sequential sum at each
  project's lanes, minus repeated builds and bootstrap. More speed needs a second Postgres
  container as a second track (for example a `postgresql2` service on another port, with the
  projects divided between them). That is a repository decision, and the declaration supports it
  without changes.
- **Polecat**: Polecat.Tests dominates (14m54s at 4 workers locally, under emulation). The
  companions run before or after it on the same track. Expect no wall-clock gain.

## Not decided: questions for Jeremy

1. **Which target frameworks does a local gate run?** CI already runs the matrix. The proposal's
   default is the newest TFM only, with `--framework all` as the opt-in. That halves Marten's and
   Fisher's gate.
2. **Retries in a gate.** The issue says a gate is "not a retry machine". The proposal keeps each
   project's own opt-in fresh-process retries (the `Retry` trait, #661), because those are there on
   evidence. Marten's `Bug_catching_up_apply_errors` fails its first attempt about 40% of the time,
   so with no retries at all, Marten's gate would go red on roughly two pushes in five. The
   alternative is `RetryBudget.None` in a gate, which is red but honest.
3. **Stop on first failure?** A fast gate could cancel the remaining units when one goes red. The
   cancelled units then report Indeterminate, using `Supervisor.Snapshot()`. The proposal leaves
   this off by default, because a full report is the point of a gate.
4. **`bobcat gate` as a tool command, or the Nuke target only?** The declaration needs C# (provider
   factories, delegates), which is why the proposal is a library type called from Nuke. A
   `bobcat gate` for repositories without Nuke would need either a project-free declaration
   (tracks and plain environment variables only) or a way to load a C# declaration. That is not
   needed by any of the four.
5. **Monitor publishing.** Each Supervisor with `PublishToMonitor` opens its own run bracket, so a
   gate would put N run cards on `bobcat ui`. Is that right, or does a gate need one parent run that
   the units fold into? The second option is a wire change, which `docs/agents/monitor-wire.md`
   says needs its own decision.

## What #729 builds

- `GateDeclaration`, `Track`, and `Gate.Run()` in `Bobcat.Supervisor`. The CPU budget and list
  scheduling sit over existing `Supervisor` instances, with the plan and predicted/actual report.
- Track preflight, including the "is this our container" check.
- A test that two units on one track never overlap, and that a unit holding several tracks waits
  for all of them, driven by `FakeWorkerFactory` with no containers.
- Adoption is each repository's own PR, moving its `runSupervised` into `Project(...)` calls. Weasel
  first, because it is the one where a gate should visibly pay.
- The ai-skills write-up, in the same piece of work as the capability, per CLAUDE.md.
