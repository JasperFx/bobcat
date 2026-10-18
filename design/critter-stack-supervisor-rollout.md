# Lessons of record: the Supervisor on Marten, Polecat, Weasel and Fisher CI

**Written 2026-10-17/18**, when the last of the four PRs for
[#565](https://github.com/JasperFx/bobcat/issues/565) merged:
[marten#5687](https://github.com/JasperFx/marten/pull/5687),
[polecat#778](https://github.com/JasperFx/polecat/pull/778),
[weasel#716](https://github.com/JasperFx/weasel/pull/716) and
[fisher#451](https://github.com/JasperFx/fisher/pull/451). Each ran its repository's CI under
`Bobcat.Supervisor` **0.60.4**, applying what `wolverine-ci-rollout.md` had learned. Everything below
was measured or observed in those PRs, and each PR body carries the full tables. Nothing here is
speculative. Where a PR reported something it could not prove, this page says so.

This is the second production rollout. The first one taught the supervisor what a CI job needs.
This one ran it on four suites with different stores and asked what still has to be written by hand,
and what the supervisor gets wrong. It found two ways a run could report red as green. Both are
fixed in 1.0 (#649, #655).

## What shipped

All four repositories reference `Bobcat.Supervisor` 0.60.4 from `build/` only, through
`Directory.Packages.props`. No shipped package and no test project gained a Bobcat reference.
0.60.4 drives every one of these MTP 1.9 / xunit.v3 3.2.2 hosts without converting them. The
supervisor speaks the wire and does not host the platform, as designed.

**Marten** ([#5687](https://github.com/JasperFx/marten/pull/5687)). It was already supervised
(0.27.3 since marten#5096), but at one worker with every failure retried.

| Where | What |
|---|---|
| `build/` | Supervisor 0.27.3 → 0.60.4. `RunTestProject(..., workers: N)`. EventSourcingTests and LinqTests run at `workers: 4`, clamped to `ProcessorCount / 2` unless `--test-workers` is passed. Every other suite stays at one worker |
| Lanes | A database per lane, `marten_testing_w{lane}`, created on demand and passed through `marten_testing_database`. The connection string is built with `NpgsqlConnectionStringBuilder`, not by text substitution |
| DaemonTests | Supervised at one worker, unchanged. Splitting it is [#562](https://github.com/JasperFx/bobcat/issues/562) |
| Retries | Opt-in only, always in a fresh process, through Marten's own `RetryOptedInTestsInFreshProcess` policy (lesson 4). The ceiling stays at 3. Two tests carry `[Trait("Retry", "3")]`, on evidence |
| Reporting | `ReleaseIdleLanes`, report-only stalls (5 min), 30 s heartbeat. `RunReport.ToText` goes in the job summary, `ToJson` to an artifact |
| Ledger | Committed per project per framework. Durations are matched **by display name** in `build/TestLedger.cs` (lesson 1). `MaxRunsPerTest = 3` |

**Polecat** ([#778](https://github.com/JasperFx/polecat/pull/778)). `Polecat.Tests` was already
supervised (0.16.0, polecat#580/#582) from a separate console runner, with every failure retried.

| Where | What |
|---|---|
| `build/` | Nuke targets `CIPolecat`, `CIAspNetCore`, `CIEfCore`, `CI`. The console runner is deleted, and its `TrxWriter` moved into `build/` so dorny/test-reporter is unchanged |
| Lanes | A catalog per lane, `polecat_w{lane}`, through `POLECAT_TESTING_DATABASE`. Each run drops and recreates the lane catalogs and their `ConnectionSource.Scoped()` siblings |
| Workers | Clamped to `ProcessorCount / 2`, but `polecat.yml` passes `TEST_WORKERS: 4` explicitly, with the evidence in a comment (lesson 6) |
| Retries | `MaxAttemptsPerTest = 2` (the old harness's real ceiling), fresh process, opt-in. One test opts in |
| Reporting | Stalls at 5 min (report), heartbeat, RSS sampling at 15 s, `ReleaseIdleLanes`. The job summary leads with flaky, failed, indeterminate, faulted and stalled tests, plus `::warning` annotations |
| Ledger | Committed per project per storage axis, folded from the PR's first green CI run. `UpdateTestLedgers --ledger-source` refreshes it by hand. `MaxRunsPerTest = 3` |
| Build fix | `build/build.csproj` clears the inherited `TargetFrameworks` (lesson 10) |

**Weasel** ([#716](https://github.com/JasperFx/weasel/pull/716)). Before this PR it ran plain
`dotnet test`, with no retries.

| Where | What |
|---|---|
| Test projects | `UseMicrosoftTestingPlatformRunner=true` once in `Tests.Build.props`. xunit.v3 stays at 3.2.2 |
| `build/` | One target per CI project: `TestCore`, `TestSqlite`, `TestPostgresql`, `TestSqlServer`, `TestMySql`, `TestOracle`, `TestFirebird`, `TestEfCore` |
| Lanes | Postgres, SQL Server and Firebird are committed at 4 workers, which is 2 lanes on hosted runners. Isolation is chosen per engine (lesson 8) |
| Retries | Opt-in, ceiling 3. No Weasel test carries the trait |
| Guards | `TheoryRowGuard`, an observer that fails the job when a collapsed theory row failed under a passing uid (lesson 2). A run that discovers nothing fails |
| Ledger | Folded from CI artifacts only (`FoldLedgers`), never from local runs |
| Test fix | `AdvisoryLockTesting.BackendsHoldingAsync` scoped to `current_database()` (lesson 7) |

**Fisher** ([#451](https://github.com/JasperFx/fisher/pull/451)). Before this PR it ran the test
executables directly, one attempt each.

| Where | What |
|---|---|
| `build/` | `./build.sh CI --framework <tfm>` builds `fisher.slnx` and runs Fisher.Tests, Fisher.AspNetCore.Tests and Fisher.EntityFrameworkCore.Tests |
| Lanes | `PartitionKey` = the discovery node's `location.type`. Fisher.Tests at 2 workers, the companions at 1. A **temp directory per lane** (`TMPDIR`/`TMP`/`TEMP`), and the leaked-database check now sweeps every lane |
| Retries | Opt-in, ceiling 3, Bobcat's default policy. No Fisher test carries the trait |
| TRX | `build/SupervisedTrx.cs` (~150 lines) writes the TRX the scoreboard check and dorny/test-reporter read, shaped like MTP's own (lesson 11) |
| Guards | `CollapsedResultsGuard`, the same idea as Weasel's guard, written independently |
| Test fixes | A non-serializable theory and a load-sensitive timing race (lessons 2 and 5) |
| Ledger | Committed from a local run, both TFMs. CI records into it and uploads it, and never commits it |

## Measured

Read every figure with three caveats:

- **CI figures are GitHub-hosted 4-vCPU runners**, where `ProcessorCount / 2` gives 2 lanes, except
  for Polecat, which ran 4 on purpose.
- **Local figures are an 18-core M-series machine that was not quiet.** Sibling agents ran the
  other three repositories' suites on the same machine at the same time (Fisher measured a load
  average of 15 to 30). Read the spread, not the decimals. Weasel alternated runs to spread the
  load.
- **The sample sizes are two or three supervised CI rounds per repository.** That is enough to see
  parity or a large effect, and not enough to rank two balancing strategies (lesson 9).

**Marten.** CI, test step in minutes. The baseline is the median of 7 green `master` runs.

| Job | Baseline | Run 1 (count) | Run 2 (local ledger) | Run 3 (CI ledger) | Workers |
|---|--:|--:|--:|--:|--:|
| EventSourcing net9 | 8.8 | 6.3 | 5.9 | **5.7** | 2 |
| EventSourcing net10 | 5.3 | 3.3 | 4.1 | 4.2 | 2 |
| Linq net9 | 2.6 | 1.4 | 1.8 | 1.8 | 2 |
| Linq net10 | 1.2 | 1.2 | 1.2 | 1.2 | 2 |
| Daemon net10 | 6.5 | 6.7 | 6.4 | 6.4 | 1 |
| Longest whole job | 10.5 | 8.8 | 8.3 | 8.6 | |

The old pole went from 8.8 to about 5.7–6.3 minutes. The pole is now Daemon at about 8.5 minutes,
untouched (#562). The workflow's wall clock was 11.0–12.7 minutes against a 9.2–13.9 baseline,
so the gain disappears into job-start staggering until Daemon is split. Locally, EventSourcingTests
(2,525 tests) ran 4m36s at 1 worker, 2m29s at 2 and 1m27s at 4. LinqTests (1,550) ran 53s, 32s and
22s. No test failed in any local or CI run. The Daemon flake below passed on its retry.

**Polecat.** CI, `default` and `edge` SQL Server axes. The baseline is 6 green main runs on
0.16.0, already at 4 workers.

| Job | Baseline test step | Round 1 (count) | Round 2 (ledger) | Round 3 (ledger) | Workers |
|---|--:|--:|--:|--:|--:|
| polecat `default` | 17.4–27.5 min (median ≈ 24.6) | 13m46s | 22m33s | 22m38s | 4 |
| polecat `edge` | 11.3–23.5 min (median ≈ 15.9) | 17m20s | 20m38s | 21m54s | 4 |
| aspnetcore, efcore | 0.3–0.7 min | 0.7–0.8 min | green | green | 1 |

All three rounds sit inside the baseline's range. The same 3,402 tests measured 50m25s of summed
test time in round 1 and 81m28s in round 2. Runner variance is larger than anything the PR changed.
Peak worker RSS was 297–309 MB per worker. Locally, with SQL Server 2025 under x64 emulation: 40m18s
at 1 worker, all green, and 14m54s at 4 workers with 4 timeouts that never occurred on CI.

**Weasel.** CI test step in seconds. The baseline is 3 green `master` runs per leg. The supervised
step includes 7–11 s of Nuke bootstrap.

| Leg | Baseline | r1 (count) | r2 (ledger) | Lanes |
|---|---|--:|--:|--:|
| MSSQL 2019 net10 / net9 | 72–95 / 81–93 | 90 / 74 | **54 / 63** | 2 |
| MSSQL 2022 net10 / net9 | 99–108 / 104–111 | 74 / 76 | **47 / 66** | 2 |
| Firebird 3 / 4 / 5 (net10) | 142–209 / 102–125 / 105–126 | 112 / 76 / 117 | 115 / 82 / 77 | 2 |
| Postgres (4 legs) | 24–44 | 31–48 | 34–48 | 2 |
| Oracle net10 / net9 | 43–68 / 42–66 | 70 / 79 | 75 / 44 | 1 |
| Core, SQLite, MySQL | 3–10 | 10–37 | 9–16 | 1 |
| EF Core | 13–25 | 23–31 | 26–29 | 1 |

SQL Server went from about 100 s to 47–66 s, Firebird improved 20–40%, Postgres and Oracle stayed
at parity, and the tiny suites got slower (lesson 10). No workflow's wall clock improved materially,
because container start, restore and build take 2–4 minutes per job. Locally, SQL Server ran 83–109 s
in process, 54–56 s supervised at 4 workers by count, and 25–36 s at 4 workers from the ledger.

**Fisher.** Local, Fisher.Tests (net10.0) and all three suites, 2,698 tests:

| | Fisher.Tests | All three suites |
|---|--:|--:|
| Direct executables, 4 runs | 18 s | 18.0–26 s |
| Supervised, 1 worker | 15.5–17.6 s | 23.8–25.2 s |
| Supervised, 2 workers (committed) | 13.3–16.0 s | 21.1–23.5 s |
| Supervised, 4 workers | 12.5–16.2 s | 20.6–23.9 s |

On CI (2 workers), the three suites took 39.1–58.8 s against a 46–72 s test step on `main`. The
`Build and test (supervised)` step took 110–127 s, and the job's wall clock was 132–156 s against
120–163 s. That is parity, as intended. Counts matched `main` exactly: 2,673 passed and 25 skipped
per TFM.

**Faults across all four.** In every supervised CI round there were no worker faults, no
indeterminate outcomes and no runner deaths. Peak worker RSS was 125–309 MB per lane, except
Weasel's EF Core suite at 600–770 MB. Two lanes on a 4-vCPU/16 GB runner never came near
Wolverine's `The runner has received a shutdown signal`. Stalls appeared only in Polecat (below).

## The lessons, in the order they were paid for

1. **The ledger's key is not portable, so a committed ledger matched nothing.** xUnit v3's MTP uid
   hashes the test assembly's path, so the same test gets a different uid for each checkout path,
   each configuration (`bin/Debug` vs `bin/Release`) and each target framework. Three repositories
   measured it independently:
   - **Marten:** a ledger committed from a local Debug run matched **0 of 2,525** uids on the
     Release runner, so CI run 1's folded ledger held 5,050 keys for 2,525 tests. net9.0 and
     net10.0 shared 0 of 2,525 locally. Run 1 logged `balancing lanes with 2525 test duration(s)`
     and then split the lanes 1263/1262, which is exactly the count split.
   - **Polecat:** 0 uids in common between a local Debug run (`/private/tmp/...`) and CI's Release
     run (`/home/runner/work/...`). CI to CI matched 3,402 of 3,402.
   - **Weasel:** 0 shared across machines at the same framework and configuration, 0 across
     frameworks on one machine, and all of them across CI legs, which share a checkout path.

   Fisher's PR says the opposite: "uids are stable across machines, so the locally recorded
   ledger applied on CI". 0.60.4 printed no count of matched durations (lesson 3), so nothing in
   that run could have shown a match. Treat the claim as unconfirmed until a run with #663's line
   says otherwise.

   This breaks the premise of `ledger-design.md` ("commit the ledger so the next run balances from
   measured durations") for xUnit v3 suites. Each repository worked around it differently. Marten
   re-keys durations by display name inside a keep-everything `TestFilter`, the only hook between
   discovery and planning, and matched 2,521 of 2,525 and 1,513 of 1,550 afterwards. The misses
   are theories with duplicate display names. Weasel and Polecat fold the ledger from CI artifacts
   only. A uid that depends on the environment is the failure class of Wolverine's lesson 6, at the
   level of the ledger. The open question is the key itself, and whether committing the ledger
   should be the default at all. That is Jeremy's decision (see below).

2. **A theory whose data xUnit cannot serialize collapses into one uid, and the last row's verdict
   won.** Every row reports under the theory's single uid with a different display name, and
   `MtpWorkerClient` kept the last terminal update per uid (`_outcomes[uid] = outcome`).
   `GuardAgainstAnUnfilteredRun` does not fire, because no uid is unexpected. **This was the
   finding that could launder red into green.** Two repositories found it on their own:
   - **Weasel:** with the first row of `TableColumnTests.can_add` forced to fail, the in-process
     host reported `failed: 1` and the supervised run reported `1056 passed`, exit 0. 48 theories
     (about 300 rows) are affected. Core alone collapses 746 results into 538 tests.
   - **Fisher:** a `TheoryData<string, object>` carrying `DBNull.Value` made discovery return 2,624
     tests instead of 2,631. With a failing first row the run was green, while a direct run said
     `failed: 1`. Fisher fixed the test by passing the shape's name and looking the value up inside
     the body.

   Both repositories wrote a consumer-side guard on `ISupervisorObserver.TestUpdated`
   (`TheoryRowGuard`, `CollapsedResultsGuard`). Two independent guards for one defect meant the fix
   belonged in Bobcat.

3. **When no known duration matched, balancing fell back to count without saying so.** Marten's run
   1 looked exactly like a balanced run. Polecat said the same. A count of how many known durations
   matched would have caught lesson 1 on the first run.

4. **`DefaultFailurePolicy` retried a `Retry`-tagged test warm, in the same process.** That
   contradicts Wolverine's lesson 5 ("the fresh process is the bracket"). With `ReleaseIdleLanes`
   it could also land in `UnsupportedDispositions`. Marten and Polecat each wrote the same ~10-line
   "opted-in tests retry in a fresh process" policy. Fisher used the default policy only because no
   Fisher test opts in. Marten also found its own `CLAUDE.md` claiming the trait *raises* a test's
   ceiling. It can only lower it: `RetryBudget.AttemptsAllowedFor` caps it at `MaxAttemptsPerTest`.

5. **Retry-free parity is the right default where the old harness never retried, and opt-in
   retries need first-attempt evidence.** Marten and Polecat moved from retry-everything to opt-in.
   Weasel and Fisher stayed at one attempt. The retry-everything harnesses had been hiding flakes:
   - **Marten:** in 40 `master` runs, two tests used every retry the old harness spent.
     `Bug_catching_up_apply_errors.should_catch_apply_event_exceptions` failed its first attempt
     in 13 of 33 green runs, and in all three of this PR's CI runs. A fresh process always passed.
     Its projection throws deterministically, so a `CatchUpAsync` that returns cleanly about 40% of
     the time is not obviously a timing problem. The trait keeps it from turning the job red, and
     does not explain it.
   - **Polecat:** five tests had passed on retry, invisibly, in 78 job logs. Only the
     subscription/coordinator test opted in. The DCB racers, the per-tenant cancel test and
     `monthly_projection_works_with_async_daemon` were deliberately left out, because a failure in
     any of them could be a real bug, and **no old log recorded why the first attempt failed**. The
     text report still did not say why (`RunReport.ToJson` had it per attempt; `ToText`, which is
     what people read, did not).
   - **Fisher:** a fresh-process retry would have hidden the `otel_counters` race (a fixed 50 ms
     sleep standing in for "the other thread holds the lock"). It failed once under load. Fisher
     reproduced it by delaying the blocker, and fixed it to wait for a signal. The deterministic
     reproduction, not a green streak, is the evidence for the fix.

6. **Clamping: measure and decide.** Wolverine learned to clamp to `ProcessorCount / 2` after four
   Marten hosts killed a 4-vCPU runner. Marten, Weasel and Fisher used that clamp unchanged, and it
   needed no tuning. Marten measured what it costs: on a 4-vCPU runner, 2 workers give about
   1.4–1.5x, where the same split gives 1.8x locally. The runner's cores are the ceiling. Polecat's
   evidence points the other way. Its old runner had bypassed its own clamp by passing
   `--workers 4`, and ran 4 SQL Server-backed hosts on 4-vCPU runners for 100 of 100 green runs at
   about 300 MB RSS per worker. Applying the clamp blindly would have halved Polecat's parallelism
   for no measured reason. So the lesson is not "always clamp". Measure peak RSS
   (`ResourceSampleInterval`) and decide. One gap: the sampler sees worker processes only. On a
   database-backed suite the database container is the large process, and `RunResources` cannot
   see it.

7. **A new bug shape for parallel suites: queries over server-wide catalogs cross lanes.** A
   database per lane isolates advisory locks, because a lock is scoped to its database. But
   `pg_locks` shows the whole server, and Weasel's `NextLockId()` restarts at 7,100,001 in every
   process. On Weasel's Postgres suite, one lane counted the other lane's backends holding the
   same id, and `TerminateBackendsHoldingAsync` **killed them**, failing whichever test in the other
   lane held that lock. Two tests failed at 2 and 4 workers locally, never in process. After
   filtering on `current_database()`: 0 failures in 24 local and 8 CI Postgres runs. This is a
   variant of bug shape #2 in `parallel-ready-suites.md` ("server-scoped names"): a test that
   **observes** a server-wide catalog (`pg_locks`, `pg_stat_activity`) crosses lanes, and one that
   **acts** on what it sees crosses them destructively. Shipped code has the same query (see "Not
   yet filed").

8. **The unit of per-lane isolation depends on the store and on the suite's literals.**

   | Store | Unit | Why |
   |---|---|---|
   | Marten (Postgres) | a database per lane | `ConnectionSource` already read the variable |
   | Polecat (SQL Server) | a catalog per lane, dropped and recreated every run with its scoped siblings | polecat#580 had already removed the text-rewritten connection strings |
   | Weasel Postgres, SQL Server | a database per lane | Schema names are the tests' own literals, and xUnit collections (six classes in `[Collection("partitions")]`) group classes the supervisor cannot see. A per-lane schema cannot fix that |
   | Weasel Firebird | nothing | Every class already creates its own database file named after itself |
   | Weasel MySQL, Oracle | 1 worker | A MySQL schema is a server-scoped database and an Oracle schema is a PDB-scoped user, both with literal names shared across classes. Both suites are short |
   | Fisher (SQLite) | a temp directory per lane | Every database lives under `Path.GetTempPath()`, so `EnvironmentFor` setting `TMPDIR` needed no test changes. A leaked file now names its lane |

   `EnvironmentFor` carried every one of these. The seam Wolverine's rollout added was enough. What
   no repository found was the bug shape Wolverine hit most: no name-coupled assertions and no
   environment-dependent test identity turned up in any of the four.

9. **Duration balancing pays only where a few heavy classes exist, and it cannot see contention.**
   - **Weasel, where it paid:** SQL Server at 4 local workers went from 54–56 s by count to 25–36 s
     from the ledger, and on CI from 62–67 s to 40–56 s.
   - **Marten, where it did not:** EventSourcingTests at 4 workers was 1m27s by count and 1m29s
     from the ledger. At 2 workers it was 2m29s and 2m18s. On CI it was inside run-to-run noise.
   - **Polecat, where it may have hurt:** round 1 (count) measured 50m of summed test time, and two
     ledger rounds measured 81m each, with the same lane plan. That is consistent with runner
     variance. It is also what you would see if packing the heavy database classes together raises
     contention on one SQL Server, which a per-test duration cannot express. Three runs cannot tell
     the two apart.

   The largest class still sets the floor, and `RunReport`'s Timing section named it each time:
   Firebird's `racing_appliers_create_a_table_and_add_a_column_and_all_succeed` (30–68% of the run),
   Weasel Postgres's `stamps_are_capped_so_the_table_cannot_grow_without_bound` (up to 60%), and
   Fisher's `the_high_water_rows_last_updated_moves_on_an_idle_daemon` (about 6.5 s). On Polecat's
   edge axis, one stalled test was 25% of the wall clock.

10. **The Nuke project costs something on small suites, and inherits the repository's build
    properties.** Weasel measured 7–11 s of Nuke bootstrap per job, which makes the 3–10 s suites
    (Core, SQLite, MySQL) net slower. Polecat's small suites lost about 0.3 minutes per job, and
    Fisher's companion suites cost more to launch than to run. Separately, Polecat's root
    `Directory.Build.props` made `build/build.csproj` multi-target, so `./build.sh` failed only on
    CI with "Your project targets multiple frameworks". Wolverine's runner had hit the same
    "plural wins" rule.

11. **CI that reads TRX needs a writer on the supervisor's side.** A worker in MTP server mode
    cannot take `--report-trx`. Polecat moved its old runner's `TrxWriter` into `build/`. Fisher
    wrote `build/SupervisedTrx.cs` (~150 lines) and checked its per-class counts against a
    `--report-trx` run of the same build. Two repositories, two writers.

12. **Skips were counted as passes.** Weasel: `Summarize()` said `1091 passed` and
    `RunReport.ToJson` said `cleanPass: 1091` for Firebird, where 6 were skips (19 on Firebird 3).
    Weasel's job summary split them out itself.

13. **Parallel efficiency counted concurrency inside a worker as lane parallelism.** Marten:
    EventSourcingTests at 1 worker with `DisableTestParallelization = true` read **1.56x** (674 s
    measured over 433 s of wall clock), so the per-test durations xUnit v3 reports must overlap.
    Fisher: 11–41x locally and 6.6–7.3x at 2 workers on CI, because xUnit already runs Fisher.Tests'
    collections in parallel in one process. Read correctly, that figure is useful: it predicted
    before any tuning that more workers would buy Fisher little (1 → 2 workers gave about 10–15%,
    and 4 was within noise).

14. **The committed ledger is large.** Every repository measured the same cause: indented JSON,
    the uid written as the key and again on each observation, and the display name and run id on
    every entry.

    | Repository | Bytes per observation | Per run kept | At the default `MaxRunsPerTest = 20` |
    |---|--:|---|---|
    | Marten | ~575 | EventSourcingTests 1.45 MB per framework. 8.4 MB for the four committed files at 2 runs | ~29 MB per framework |
    | Polecat | ~560 | 1.79 MB per file, two files | ~70 MB per refresh |
    | Weasel | ~450 | 8.5 MB for one CI fold at retention 3 | Postgres alone ~19 MB |
    | Fisher | ~525 | Fisher.Tests 1.4 MB per framework | ~28 MB per framework |

    Every repository set `MaxRunsPerTest = 3`. Weasel found a second effect: `Merge` keeps the
    **larger** retention, so one artifact written with a longer retention grows the committed file
    for good. Its `FoldLedgers` uses `Record` into the committed file's own retention instead.

15. **The CI plumbing around `SupervisorResults` is copied by hand in every repository.** Marten,
    Polecat, Weasel, Fisher and Wolverine each wrote their own GitHub job-summary table,
    `::warning` annotations, "zero tests discovered fails" guard, and lane-database provisioning
    loop (or temp-directory loop). Counting Wolverine, that is five copies of the same few hundred
    lines.

## Where the open items landed (2026-10-18)

**Fixed in 1.0**, all by [#665](https://github.com/JasperFx/bobcat/pull/665) ("1.0 milestone fixes",
merged 2026-10-16, one commit per issue):

- **[#649](https://github.com/JasperFx/bobcat/issues/649), collapsed theories (lesson 2) — fixed.**
  Several results under one test id are folded worst-first, never last-wins. The text report lists
  "Collapsed theories" with their rows, and the JSON carries `collapsed` / `collapsedRows`. Weasel's
  `TheoryRowGuard` and Fisher's `CollapsedResultsGuard` can go once those repositories take 1.0.
  Separately, [#569](https://github.com/JasperFx/bobcat/issues/569) gave a projected theory row its
  own spec identity. That is the projected-lane half of the same problem.
- **[#655](https://github.com/JasperFx/bobcat/issues/655), skips counted as passes (lesson 12) —
  fixed.** `RunOutcome.Skipped`, with its own count in `Summarize()`, the JSON summary, a trailing
  optional `Skipped` on `run_finished`, and the run card.
- **[#661](https://github.com/JasperFx/bobcat/issues/661), warm retries by default (lesson 4) —
  fixed.** A `Retry` trait now retries in a fresh process. `[Trait("RetryWarm", "true")]` opts into
  a warm retry. `RetryBudget` is unchanged, so the trait still only lowers the ceiling;
  `docs/bobcat-tool.md` states that `--retries` is a ceiling.
- **[#662](https://github.com/JasperFx/bobcat/issues/662), why a pass-on-retry first failed
  (lesson 5) — fixed.** Each pass on retry names its first attempt's exception type and a message
  bounded to 200 characters in the text report, and `recoveredFrom` in the JSON. Retry and isolation
  log lines now print the display name, not the uid hash Polecat saw in 0.16.0.
- **[#663](https://github.com/JasperFx/bobcat/issues/663), how many known durations matched
  (lesson 3) — fixed.** `Known durations matched N/M test(s)`, or `0/M: lanes balanced by test
  count`, in the text and JSON reports, the log, and `ISupervisorObserver.LanesPlanned`.
- **[#664](https://github.com/JasperFx/bobcat/issues/664), parallel efficiency (lesson 13) —
  fixed.** Split into lane concurrency (worker busy time over wall clock) and in-worker concurrency
  (measured over busy). A one-lane run says the overlap was all inside the worker.

**Filed, open:**

- **[#693](https://github.com/JasperFx/bobcat/issues/693)**, the package-split follow-up from
  [#690](https://github.com/JasperFx/bobcat/issues/690). `Bobcat.Supervisor` carries Bobcat's
  JasperFx ≥ 2.90.2 floor into a build tool, so a Nuke build that pins an older JasperFx fails with
  NU1605, although the supervisor needs only the `Bobcat.Resilience` types. Found applying 1.0 to
  Marten's DaemonTests. The same issue (#690) records that moving `Bobcat.Xunit` to 1.0 means
  xunit.v3 4.x / MTP 2 for the projects that take it, which Marten's lesson 9 had named.
- **[#562](https://github.com/JasperFx/bobcat/issues/562)**, Marten's DaemonTests, now its CI
  pole.
- **[#566](https://github.com/JasperFx/bobcat/issues/566)**, the local commit-and-push gate. #565
  named this rollout as its input.

**Decided (Jeremy, 2026-10-18): the ledger's key, and whether committing it is the default
(lessons 1 and 14).** The ledger keys an observation on the test's **display name**, which is stable
across machines (Marten matched 2521/2525 this way), and falls back to the uid only where two tests
share a display name. Projected specs key on their spec identity, which is their display name. It
is **not committed by default**: CI uploads it as an artifact and the next run downloads it.
Committing is opt-in, and the default retention drops to about 5 runs. Tracked as
[#730](https://github.com/JasperFx/bobcat/issues/730), which also updates `ledger-design.md` (it
still says uid key and recommends committing). [#698](https://github.com/JasperFx/bobcat/issues/698)'s
compact format builds on it. Same day: [#693](https://github.com/JasperFx/bobcat/issues/693), the
dependency-light Supervisor, moved into 1.0, and [#566](https://github.com/JasperFx/bobcat/issues/566)
ships its design note in 1.0 with the build in [#729](https://github.com/JasperFx/bobcat/issues/729)
(Post-1.0).

**Not yet filed, in Bobcat.** Each was reported in a PR body and has no issue. A person should
decide whether each is worth one:

- A compact on-disk ledger form (unindented, uid only as the key, display name once per test), and
  `Merge` keeping the larger retention (lesson 14).
- A supervisor-side TRX writer beside `RunReport` (lesson 11).
- A GitHub job-summary helper and a lane-database helper, to replace the copied plumbing
  (lesson 15).
- `parallel-ready-suites.md`: the server-wide-catalog variant of bug shape #2 (lesson 7), "measure
  RSS and decide" beside the clamp (lesson 6), "measure parallel efficiency first" (lesson 13), and
  a note that duration balancing needs a few heavy classes to help (lesson 9).
- RSS sampling that cannot see the database container (lesson 6).
- The Nuke bootstrap tax on tiny suites, and the inherited-`TargetFrameworks` trap, in the
  supervisor's "running from Nuke" guidance (lesson 10).
- `RunReport.ToText` leading with stalls the way it leads with retries. Polecat's
  `flat_table_works_with_async_daemon` stalled in 10 of 78 jobs and appeared only as a log line in
  green jobs.
- A "runner speed index" (this run's summed time against the ledger median) and a way to compare
  two lane plans' predicted and realised makespans, to settle Polecat's count-vs-ledger question
  (lesson 9).

**Not yet filed, in the four repositories.** The agents reported these and did not open issues:

- **Weasel: `AdvisoryLock.FindHolderAsync`** (`src/Weasel.Postgresql/AdvisoryLock.cs`) ships the
  same unscoped `pg_locks` query that lesson 7 fixed in the tests. On a server where several
  databases use the same lock ids (database-per-tenant, for example), it can name a holder in
  another database. No test failed on it, and the PR did not change product behaviour.
- **Polecat: `flat_table_projection_tests.flat_table_works_with_async_daemon`** hangs past 300 s in
  about 13% of runs (10 of 78 historical jobs, and 2 of this PR's 6 big-suite jobs), then passes.
  Its own waits add up to about 90 s, which points at daemon start or dispose rather than a slow
  catch-up. It was reported, not retried and not investigated.
- **Marten: `Bug_catching_up_apply_errors.should_catch_apply_event_exceptions`** fails its first
  attempt in about 40% of runs (13 of 33, plus all 3 of the PR's runs) and always passes in a fresh
  process. The lead is state left by an earlier test in the same process. It carries
  `[Trait("Retry", "3")]` and is reported `[FLAKY]` each time.

The PRs also reported these, smaller and also unfiled: Weasel's Postgres "Case Sensitive" matrix
axis is a no-op (the variable is set on the service container, not the test process), and
`Weasel.CommandLine.Tests` is run by no workflow. Polecat's `start_stream_with_duplicate_id_throws`
got a raw `SqlException` instead of `ExistingStreamIdCollisionException` when the insert timed out,
locally under emulation only.
