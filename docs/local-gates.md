# Local commit-and-push gates

One command that runs **every** test project of a repository before a push, as fast as the machine
allows, without two suites colliding on a shared container. A `GateDeclaration` in
`Bobcat.Supervisor` lists the projects, the resource **tracks** each one holds while it runs, and one
CPU **worker budget** across all of them; `Run()` builds once, preflights every track, then runs one
[supervisor](parallel-ready-suites.md) per `(project, framework)` unit -- longest first, never two on
one track, never more workers than the budget -- and exits with the worst unit's exit code and a
report that names every unit.

The design of record is `design/local-push-gates.md` in the repository (bobcat#566); the decisions
that fixed the open questions are on [bobcat#729](https://github.com/JasperFx/bobcat/issues/729).

## A declaration

In C#, beside the targets that already configure each project -- a Nuke target, or a small console
program. The per-project facts a gate needs (workers, lane isolation, the retry policy) are delegates
and provider factories that neither MSBuild items nor traits can carry.

```csharp
using Bobcat.Supervisor;

var gate = new GateDeclaration
{
    // A track is a resource a unit holds while it runs. Exclusive: capacity 1.
    Tracks =
    {
        Track.Listening("postgres", "localhost", 5433),
        Track.Listening("sqlserver", "localhost", 1434),
        new Track("rabbit").Check("the broker answers", () => Rabbit.Ping()),
    },
    WorkerBudget = Environment.ProcessorCount / 2,      // the default
    Frameworks = FrameworkSelection.Parse(framework),   // null → newest; "all"; "net9.0"
    Configuration = "Release",
    Solution = "wolverine.slnx",                        // built once; each project its own frameworks
    RunTagPrefix = "local-gates/wolverine-gate",        // default: BOBCAT_RUN_TAG
};

gate.Project("src/Testing/CoreTests", workers: 2)
    .On("postgres")
    .Supervise((unit, supervisor) =>
    {
        supervisor.TestFilter = NotFlaky;
        supervisor.RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 };
        supervisor.AddFailurePolicy(new RetryFailuresInFreshProcess());
        supervisor.StallThreshold = TimeSpan.FromMinutes(5);
    })
    .LaunchingWith(unit => new MtpWorkerFactory(unit.Executable)
    {
        ConnectionStrings = laneConnectionStrings(unit.Workers)
    });

gate.Project("src/Persistence/EfCoreTests").On("postgres", "sqlserver");   // holds both
gate.Project("src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests", workers: 4).On("rabbit");
gate.Project("src/Testing/CoreTests.Pure");                                 // no track: the budget alone

var results = await gate.Run();
return results.ExitCode;
```

The rules:

- **A track is exclusive.** A unit runs only while it holds every track it names; units on one
  track run one at a time, tracks run concurrently with each other, and a unit holding several
  (Weasel's EF Core suite needs three databases) waits for all of them. Capacity is 1 and the
  declaration does not offer more: names, not containers, are what make two projects collide on a
  server, a scheduler cannot see names, and nothing measured says two projects overlapping on a
  container beat running them one after the other at full lanes.
- **A project with no track** is limited only by the budget.
- **The budget is one number across every unit.** A unit asking for more workers than the budget
  is clamped to it, never held back; a smaller unit fills the gap beside a larger one.
  **Workers are database connections too.** Every lane of every unit running at once opens its own
  pool, so units that share one Postgres can exhaust its `max_connections` well before they exhaust
  the CPU (Marten measured four lanes of its event-sourcing suite alone peaking above 100). The gate
  does not know what a worker connects to; if your suites share a server, size `WorkerBudget` to
  its free connections as well as to cores -- Marten's gate reads `max_connections` minus the
  connections already open and clamps its budget to that.
- **A target framework is part of the unit.** `(project, framework)` is what is scheduled, and two
  frameworks of one project hold the same tracks. By default a project runs on its **newest**
  framework only (CI runs the matrix); `FrameworkSelection.All` -- `--framework all` on a Nuke
  parameter -- runs every one, and `FrameworkSelection.Only("net9.0")` pins one, with a project that
  does not target it falling back to its newest.
- **Each project keeps its own supervisor configuration.** `Supervise` hands you the
  `Supervisor` the gate built for the unit, after the gate's defaults and `GateDeclaration.Configure`,
  so the retry budget, the policy, the filter, stall and heartbeat settings and `KnownTestDurations`
  are the project's -- the same code its CI target already has. The gate adds no retry a project's
  policy would not make. Only `MaxParallelWorkers` is the gate's: a project may run on fewer lanes
  than it was charged for, never more. `LaunchingWith` replaces the default `MtpWorkerFactory` over
  the built host when a project needs lane connection strings or a kill hook.
- **A track nobody declared is an error** when units are resolved, not a unit that waits forever.

## What `Run()` does, in order

1. **Preflight every track a unit names**, before anything is built or launched. A failed check is
   reported by track and the gate aborts with exit code 2 -- nothing ran, so nothing is known.
   `Track.Listening` is the cheapest honest "is the container up"; add a `Check` for anything more
   (a schema, a queue, a compose project name). The gate never starts, stops or resets a container
   on its own; a track's `Heal` (below) is the one place it does, and only as the build told it to.
2. **Build once**, then every host runs as built. With a `Solution` that is one
   `dotnet build <Solution> -c <Configuration>` with **no `-f`**, so every project builds its own
   `TargetFrameworks` -- a netstandard2.0 source generator or a net9.0-only library beside the test
   projects builds as it always does ([bobcat#774](https://github.com/JasperFx/bobcat/issues/774);
   `-f` on a solution applies to every project in it and fails those with `NETSDK1005`). Without a
   solution it is `dotnet build <project> -c <Configuration> -f <framework>` once per unit, which
   only ever names a framework the project targets. Whoever built them, the gate then checks that
   every selected unit's host exists and aborts naming the path it looked for if one does not.
   - **`Build` is the escape hatch** when the default is wrong for your repository -- a solution
     too large to build whole before a push, a build that needs properties, or a Nuke
     `DotNetBuild` you already have. It is called once with a `GateBuild` (the solution, the
     projects, the frameworks the gate runs, newest first, and the configuration); throw to fail the
     gate. The host check still runs after it.
   - `SkipBuild = true` is the promise that the hosts are already there.
3. **Print the plan**: every unit in the order it will be offered to the budget, with its workers,
   tracks and predicted duration.
4. **Run, longest first.** Whenever a unit finishes, the longest waiting unit whose tracks are free
   and whose workers fit starts. Predictions come from the gate's own record of previous runs
   (`DurationsPath`, `.bobcat/gate-durations.json` by default: the mean of the last five wall clocks
   per unit, on this machine); a unit with no record goes **first**, because a short unknown started
   early costs nothing and a long unknown started last is the whole makespan. Every unit runs -- a
   red unit does not cancel the rest, because a full report is the point of a gate.
5. **Report.** One line per unit with its verdict mark, wall clock, predicted against actual and
   counts; then each not-clean unit's full `RunReport`; then the totals and the exit code.

```
Gate: 4 unit(s), 4 worker(s) budgeted, 6m12s wall clock (11m40s one after the other)
  ✓ Wolverine.RabbitMQ.Tests.net10.0   5m58s (predicted 6m05s)  212 passed, 4 worker(s)
  ~ CoreTests.net10.0                  3m10s (predicted 3m02s)  388 passed, 1 passed on retry, 2 worker(s)
  ✗ EfCoreTests.net10.0                1m52s (first run)        90 passed, 2 failed, 1 worker(s)
  ✓ CoreTests.Pure.net10.0               40s (predicted 38s)    140 passed, 1 worker(s)

=== EfCoreTests.net10.0 ===
…the unit's RunReport…

Exit code 1: 2 clean, 1 passed on retry, 1 failed, 0 indeterminate, 0 not run.
```

The exit code is the worst unit's in the supervisor's own convention -- **2** (indeterminate,
aborted, a unit that never ran or whose supervisor threw) over **1** (failed) over **0**. A pass on
retry (`~`) is exit 0 and is reported as flaky, never folded into clean.

## Healing a track

A long-lived Docker service degrades without ever failing: Wolverine's Azure Service Bus emulator
went from ~2 seconds a test to ~2 minutes a test once its SQL backend had piled up state, and a
5-day-old SQL Server with 313 schemas sat at 328% CPU while every query waited to compile. Both
answered their health checks, every test still finished inside the 5-minute stall threshold, and
both had to be recreated by hand ([bobcat#824](https://github.com/JasperFx/bobcat/issues/824)).
The run was **slow, not stuck** -- a stall threshold cannot see it, and `StallAction.KillAndRetry`
kills the *worker*, which cannot help when the *service* is the problem.

A track can declare how to heal its service. The unit's supervisor reads the degradation signal; the
gate, which owns the track, runs the heal:

```csharp
Track.Listening("sqlserver", "localhost", 1434)
    .Heal(async (heal, ct) =>
    {
        // Recreate the containers with fresh volumes and wait until they answer...
        await Docker.Compose("-p wolverine up -d --force-recreate --renew-anon-volumes sqlserver", ct);
        await Docker.WaitHealthy("wolverine-sqlserver-1", TimeSpan.FromMinutes(2), ct);

        // ...then re-run the setup a fresh container has lost: recreating SQL Server drops the
        // lane catalogs the build created at the start of the run, and without them every
        // resumed test would fail instantly.
        await LaneDatabases.SqlServer(SqlClientFactory.Instance, adminConnectionString, "wolverine_w")
            .EnsureCreated(heal.Unit.Workers, ct: ct);
    });

Track.Listening("asb", "localhost", 5672)
    .Heal(ct => Docker.Compose("-p wolverine up -d --force-recreate --renew-anon-volumes asb-sql asb-emulator", ct));
```

What happens, in order:

1. **The signal.** A test is *slow* when it ran more than `DegradationFactor` (default **10**)
   times its duration in the supervisor's `KnownTestDurations` -- the ledger the lanes are already
   balanced on -- and at least `DegradationFloor` (default **1s**, so a 5ms test taking 60ms is
   jitter, not a signal). The run is **degraded** when the last `DegradationWindow` (default **5**)
   completed tests with a known duration were all slow, or when a stall fires. Tests with no known
   duration do not count, so **a first run can never degrade**: there is nothing to be slow
   against. The supervisor logs `DEGRADED: the last 5 completed test(s) with a known duration each
   ran more than 10x it: 1m58s against 9.5s known (12.4x over 5 test(s)) -- ...`, fires the
   `Degraded` observer callback and collects it on `SupervisorResults.Degradations`.
2. **Report-only unless the track declared a heal.** A track with no `Heal` changes nothing: the
   run goes on, slow, and its report ends with a `Degraded` section. Same guardrail as a stall under
   `StallAction.Report`.
3. **Pause.** With a heal declared, the supervisor kills every live worker (`PAUSING the run to
   heal`) so nothing is mid-test while the service is recreated. The killed attempts are
   `HealInduced` -- a sibling of the stall kill's `StallInduced` -- so they never count as flakes,
   never go to quarantine and never spend the `RetryBudget`: the kill was the supervisor's own act
   and the service, not the test, was the problem.
4. **Heal, then preflight again.** Once the killed workers' results are recorded and every lane is
   torn down, the gate runs the heal of every track the unit holds that declared one, in
   declaration order (`healing track sqlserver (heal 1 of 2): ...`), then **re-runs that track's
   preflight**. A recreated service that does not answer is the one state worse than a degraded one,
   so a failed preflight aborts the unit: `track sqlserver failed its preflight after being healed
   (heal 1), so the unit is aborted rather than resumed against a service that does not answer`. A
   heal that throws aborts the same way.
5. **Resume.** The test that was in flight when the kill landed is retried **alone in a fresh
   process**; the tests the dying lane never reached resume **in their lane** on a fresh worker.
   The run then measures the pace of the next `DegradationWindow` tests, which is the evidence the
   heal worked, and reports both: `Tracks healed: sqlserver (heal 1) -- ...; before: 1m58s against
   9.5s known (12.4x over 5 test(s)); after: 10s against 9.5s known (1.1x over 5 test(s))`. The
   unit's gate line says `1 track heal(s)` even when it passed clean.
6. **Bounded.** `Track.MaxHeals` (default **2**) is per track per gate run -- the service is the
   track's, and the state that degrades it piles up across every unit that ran on it, so a heal
   early in the gate and another late in it are both plausible; a third degradation is the service
   saying something a restart does not fix. Past it, the next degradation aborts the unit with
   `track sqlserver degraded again after being healed 2 time(s) this gate run (MaxHeals = 2), so
   the unit is aborted rather than recreating its service a third time: ...`. Other units keep
   running; the unit is reported as the gate reports any aborted unit (`?`, exit 2).

A stall on a healable track is healed rather than escalated through `StallAction` -- the heal is
the kill-and-retry-alone with the service recreated in between. A heal never touches another
track's service: units on one track run one at a time, the heal runs while the degraded unit is
paused, and the hook is the track's own. Two services one hook would recreate together belong on
one track. Every heal is published as `track_healed` (see [What a Run Publishes](monitor-design.md)).

The seam, for a build that is not a gate: `Supervisor.OnDegraded` is the callback the supervisor
awaits with the run paused; `Gate` sets it for a unit whose track declared a heal, and a
`Supervise` hook that sets it replaces the gate's.

## Each unit is its own run on the monitor

Every unit publishes to whatever `BOBCAT_MONITOR_URL` names (`PublishToMonitor` is on by default;
a gate is a real entry point, not a unit test) under its own tag:
`{RunTagPrefix}/{project}.{framework}`. `RunTagPrefix` defaults to the process's `BOBCAT_RUN_TAG`,
which is how a coordination plan's node becomes one run card per unit under it -- the seam is
`Supervisor.RunTag` ([bobcat#768](https://github.com/JasperFx/bobcat/issues/768), see
[What a Run Publishes](monitor-design.md)), so the units of one gate never all claim one node.
`MonitorSink` substitutes a sink, for a test.

## What it does not do

- It does not share a track. Measure first; see the design note.
- It does not stop on the first red unit. Cancel the run to stop it; a cancelled gate stops starting
  units, waits for the running ones, and throws.
- It does not retry anything a project's own policy would not retry, and it does not fail a build
  on a duration.
- It does not start containers. `./build.sh Docker`, or your compose file, does. It recreates one
  only when a unit on a track degrades and that track declared a `Heal`, and only as that heal says.
