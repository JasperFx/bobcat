# Reliable Integration Testing

**The problem:** a large integration suite that takes too long and fails for reasons that have
nothing to do with the change under test. Rerunning it usually works, which is exactly what makes
it corrosive -- the suite stops being evidence and becomes a toll.

**What you will build:** the same suite, split across worker processes, with the shared state that
made it flaky isolated per worker, and with the remaining flakiness reported honestly instead of
retried away.

The tool for all of that is the **[Bobcat Supervisor](https://jasperfx.net/news/introducing-bobcat-supervisor)**,
Bobcat's primary tool for *making* an existing suite of integration tests reliable rather than
writing a new one: it drives your test executable as worker processes, isolates resources per
worker, retries only what is allowed to be retried, and reports what it saw. The reference for it is
[Making a test suite safe to run in parallel worker processes](../parallel-ready-suites.md); this
page is the path through it.

This tutorial is written from four real suites -- Wolverine's `PersistenceTests` and `Redis.Tests`,
Polecat's `Polecat.Tests`, and Bobcat's own -- so the failure modes below are what actually broke,
not what might.

## What this actually buys

| Suite | Tests | Sequential | 4 workers | Result |
|---|---|---|---|---|
| Polecat.Tests | 1587 | 954s | **366s (2.6x)** | 1587/1587 after two fixes |
| Wolverine PersistenceTests | 78 | 164s | **73s (2.2x)** | 78/78, no source changes |
| Wolverine Redis.Tests | 144 | 452s | **206s (2.2x)** | 144/144, no source changes |

**Compute your ceiling before provisioning a fleet.** Partitioning is by test class, so a class
cannot be split, so the largest class is a floor no worker count goes below:

```
ceiling = sum(all test durations) / largest class's total duration
```

Redis is 599s of test time with a 188s compliance class in it -- a 3.19x ceiling and a ~188s floor.
Measured at 8 workers: 199s. If your ceiling is 3x, asking for 8 workers buys containers and
nothing else. When the floor is what binds, the fix is splitting that class or making its tests
faster, not adding lanes.

## Step 0 -- be an MTP host

The supervisor drives the test **executable** over Microsoft.Testing.Platform's server mode. It
never loads your test assembly, so **your test project needs no reference to Bobcat at all.** It
does need to be an MTP host rather than an xUnit native runner:

```xml
<OutputType>Exe</OutputType>
<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
```

Check with `./YourTests --list-tests`. If that errors with `unknown option`, you still have the
native runner and the supervisor cannot drive it.

The quickest way to try it is the `bobcat` tool: `bobcat run --workers 4` drives the spec project's own
test host as four worker processes ([Supervised runs](../bobcat-tool.md#supervised)). Per-worker
resources (Step 2) need a supervisor host of your own -- a small console app around `Supervisor` and
`MtpWorkerFactory`.

This changes only the entry point. `dotnet test` still runs the project, with the root `global.json`
putting it in MTP mode, and the platform's own options (`--filter-class`, `--report-trx`) replace the
VSTest ones. Under the supervisor a worker writes no TRX of its own, so a pipeline that reads one
writes it from the results with `RunReport.WriteTrx`
([A TRX file for CI that reads one](../parallel-ready-suites.md#a-trx-file-for-ci-that-reads-one)).

> **Put the property in `Directory.Build.props`, not a per-job `-p:` override.** Same output path,
> different entry point: a build without it silently produces a non-supervisable executable.

## Step 1 -- the partitioning contract

The supervisor splits **by test class**, never by individual test. That is a correctness rule
rather than a tuning choice -- every framework's isolation contract is per class or collection, so a
class's fixtures and static state assume one process.

Splitting Wolverine's suite per *test* failed 1–4 tests non-deterministically. Per *class* it was
78/78 at the same wall clock. The mechanism was a class whose setup read:

```csharp
var schemaName = "sqlserver" + ++count;   // static int
```

Split across four processes, each restarts at zero and all four collide. **Nothing about that is
visible in the test list**, which is why the rule has to be conservative. You inherit this for free.

## Step 2 -- isolate resources per worker

Class-level partitioning keeps a class together. It cannot stop two *different* classes that share a
database from landing in different workers. So each worker needs its own, set on the
`MtpWorkerFactory`:

<<< ../../src/Bobcat.Supervisor.Tests/SupervisorEndToEndTests.cs#worker-connection-strings{cs}

Each worker is launched with `ConnectionStrings__postgres`, the .NET Aspire convention, so the suite
reads it with `configuration.GetConnectionString("postgres")` -- always a whole connection string,
never a fragment to stitch onto a base one. A resource every worker shares, the broker above, takes
a plain string. `Lane` is bounded by `MaxParallelWorkers`, so you provision as many databases as
workers you asked for, and `LaneDatabases` does that provisioning on PostgreSQL or SQL Server,
dropping and recreating one database per lane before the run:

<<< ../../src/Bobcat.Supervisor.Tests/LaneDatabasesTests.cs#lane-databases{cs}

`EnvironmentFor` covers anything that is not a connection string
([Step 2 in full](../parallel-ready-suites.md#step-2-per-worker-resource-isolation)).

**Check first whether you already have this for free.** A suite that starts its own container per
process -- Testcontainers from a `[ModuleInitializer]`, say -- already gets a private broker per
worker with nothing to wire. That is why Wolverine's Redis tests parallelised with no source
changes at all.

Two costs worth knowing rather than discovering: every process pays the container start on the
critical path, and nothing reaps those containers if the reaper is off -- one orphan per process,
which reached 49 on one machine.

## Step 3 -- expect these three bugs on the first red run

All three were found in real suites, all silent, and all are why a first parallel run goes red:

1. **Text-rewriting a connection string** instead of using a builder.
2. **Server-scoped names** -- a schema, a queue, a topic -- that are unique per process but not per
   server.
3. **Environment-dependent test identities**, where a test's own name or id changes with the
   machine or the lane.

The triage order for a red first run, and what a hang or a memory bloat means, is in
[Making a test suite safe to run in parallel worker processes](../parallel-ready-suites.md).

## Step 4 -- report flakiness instead of hiding it

Two mechanisms, and the difference between them matters.

**A retry budget** lets a test pass on a second attempt. The run still exits 0, and the
pass-on-retry is reported separately rather than folded into the pass count. In a supervisor host
of your own it is the `RetryBudget`; from the tool it is `bobcat run --retries 1`, which is the same
two attempts:

<<< ../../src/Bobcat.Supervisor.Tests/SupervisorObserverTests.cs#retry-budget{cs}

The budget is a ceiling, not a switch: only a test carrying a `Retry` trait (`[Trait("Retry", "2")]`)
or a recovery hint matching the failure (`[ClearsOnRetry(typeof(TimeoutException))]`) is ever
retried, and a `SpecCatastrophicException` never is.

A `Retry` trait retries **in a fresh process**, alone. That is the safe default for an integration
test: a failure that leaves state behind (a handler registry, a static, a half-built schema) would
fail every warm retry with the first attempt's leftovers, and a worker process has no other reset.
Add `[Trait("RetryWarm", "true")]` to retry in the same process instead, for a test you know leaves
nothing behind.

Watch the pass-on-retry number, not the pass number. A retry budget will mask a rising failure
count if nobody is looking at it.

**Indeterminate outcomes are reported as indeterminate.** When a worker faults, the tests it never
reported are not silently dropped and not counted as passes -- they come back as indeterminate, with
their display names. A suite that cannot tell you what happened should say so.

**The ledger** (`TestLedger`) is where flake and duration history lives across runs, so "this test is
flaky" becomes a claim with evidence behind it rather than a memory. It is derived, never primary.

## What this does not buy you

Parallel workers do not fix a suite whose tests genuinely depend on each other's data. They expose
it. That is the point -- but it means the first run after you turn this on is a triage exercise, and
budgeting for it is the difference between adopting this and abandoning it.

## Where to go next

- Wiring it into the pipeline -- [Integrating Bobcat with CI](continuous-integration.md)
- The full parallel-readiness checklist -- [Parallel-Ready Suites](../parallel-ready-suites.md)
