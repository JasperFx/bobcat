# Lessons of record: the first production Supervisor rollout (Wolverine CI)

**Written 2026-07-31/08-01**, at the end of the session that replaced Wolverine's Nuke test harness
with supervised runs — [wolverine#3758](https://github.com/JasperFx/wolverine/pull/3758), Bobcat
0.6.0 and 0.6.1 published to nuget.org the same night. Everything below was measured or observed on
that PR's CI rounds; nothing is speculative. It was the pickup document for the sessions that
followed; the closing section records where its open items landed.

## What shipped

| Where | What |
|---|---|
| Bobcat 0.6.0 | `Supervisor.TestFilter` — supervisor-level equivalent of `dotnet test --filter`; first packaged release of `Bobcat.Supervisor` |
| Bobcat 0.6.1 | `Supervisor.ReleaseIdleLanes` (#81) — dispose idle lane workers before fresh-process retries |
| Wolverine | `build/SupervisedTests.cs` replaces the TRX-parse-and-retry harness for all ~30 CI targets; every `*Tests` project is an MTP host; `Servers.cs` connection strings env-overridable; CIMarten at `workers: 4` (clamped to 2 on hosted runners) with a Postgres database per lane |

## Measured

Local (M-series, net9.0): SqliteTests 161/161 at 4 workers, 71s → 36s (1.97x; floor = one 48-test
class). MartenTests 543+1 → later 544 clean, 3m56s at 4 workers vs an 18m CI job.

CI (GitHub-hosted, final round, 31/32 green): **CIMarten 18m → 9.5m, 544 + 12 passed, 0 retries.**
Every other job at 1 worker within ±1m of baseline — parity, as intended. Workflow wall clock
18m → 16.9m; **CIAzureServiceBus is now the sole pole**, and its 10 pass-on-retries all sit in one
class (`TopicAndSubscriptionWithCustomRuleSendingAndReceivingCompliance`), which is where
Wolverine's `Flaky`-tag removal should start. The one red, CIKafka's
`dead_letter_message_has_exception_headers`, predates the branch and fails identically sequential.

## The lessons, in the order they were paid for

1. **A supervisor needs a test filter** (0.6.0). CI targets live and die by `Category!=Flaky` and
   shard slices; without `TestFilter` the supervisor could not own a real CI job at all. A shard
   filter matching nothing must *fail* — Wolverine treats an empty match as a renamed-namespace
   error, the inversion of `GuardAgainstAnUnfilteredRun`.

2. **nuget.org propagation races CI.** The PR's first two matrix rounds failed on `NU1101` because
   the jobs started 15 seconds after `gh pr create`, before the freshly-pushed package was
   indexed — and a rerun 90s later *still* lost. The fix that worked: gate the push on the
   registration blob (`v3/registration5-gz-semver2/<id>/index.json`) plus a ~3 minute settle.

3. **Committed worker counts are tuned on a developer machine; the machine running decides.**
   Four Marten hosts beside Postgres killed a 4-vCPU/16GB runner outright. Wolverine clamps to
   `ProcessorCount / 2` unless `--test-workers` explicitly overrides. The failure signature to
   recognize: `The runner has received a shutdown signal` — that is the runner dying, not a test.

4. **Idle lanes + a fresh-process retry = `workers + 1` peak processes** (#81 → `ReleaseIdleLanes`,
   0.6.1). Lane processes sit at peak working set through the retry passes; both runner deaths were
   timestamped *during a retry*. Lanes are released only when nothing pending needs one, and a
   `RetryInProcess` decided after the release is recorded in `Unsupported`, never silently rerun.

5. **Warm-process retries cannot work for tests whose failure pollutes the process.** Tried as the
   memory fix before #81 existed; MySqlTests failed all three attempts with leftovers of attempt
   one (`end_to_end_from_scratch` cannot run "from scratch" in a process where scratch no longer
   exists — "No known message handler", "Sequence contains no matching element"). This is the
   engine's own doctrine confirmed from outside: every attempt needs the full reset bracket, and
   xUnit workers have no reset bracket, so out there the fresh process *is* the bracket.

6. **Test identity must be a function of the code alone, never the environment** — bug shape #3 in
   `parallel-ready-suites.md`. Theory data computed from a per-worker environment variable gives
   the same test a different uid in discovery than in the executing lane. Symptom: *"the worker
   finished without reporting a result for this test"*; it masquerades as an ordinary flake
   whenever the retry process happens to compute the environment discovery did. Fix pattern: a
   stable sentinel argument (`"default"`) resolved inside the test body.

7. **Bug shape #1 (name-coupled assertions) is real and plentiful**: nine MartenTests asserted
   URIs or connection strings built from the literal default database name. All fixed by deriving
   expectations from the configured connection string (`Servers.PostgresDatabaseName`). One subtle
   variant: an *ordered* expected list must be re-sorted once a name in it becomes variable.

8. **Match the old harness's real retry ceiling, not its apparent one.** Wolverine's TRX harness
   effectively allowed **three** attempts (suite pass + two single-test invocations). The
   supervised budget's initial `MaxAttemptsPerTest = 2` was silently *stricter*, and chronic ASB
   emulator flakes that used to pass on the third try surfaced as hard failures. Parity restored
   at 3 — with every extra attempt in the `[FLAKY]` ledger instead of invisible.

9. **The honest-reporting design paid rent immediately.** Indeterminate-with-forensics identified
   both runner deaths; the flaky ledger localized ASB's instability to one class; CIKafka's
   pre-existing failure now prints its name and exception in the job summary. Nothing was
   laundered.

## Where the open items landed (updated 2026-09-03)

The list this section replaced is in git history (`git log -- docs/wolverine-ci-rollout.md`);
what matters now is what became of it:

- **[#82](https://github.com/JasperFx/bobcat/issues/82) — fixed** (0.7.0): synthesized
  never-reported outcomes carry the discovery display name, so an indeterminate prints as a test
  name rather than a uid hash.
- **The durations ledger is built** (`TestLedger`, `docs/ledger-design.md`): a committed,
  merge-friendly store of per-run observations. `ledger.KnownDurations()` feeds
  `Supervisor.KnownTestDurations`, so the lane balancer works from measured durations on the
  first pass instead of going unused as it did all this session; the same store carries duration
  trends (#142) and flakiness evidence for hint proposals (#44).
- **The supervisor grew the observability this rollout ran blind without** (0.7.0–0.9.1):
  `ISupervisorObserver` live narration (#84), worker pids surfaced (#146), stall detection and
  the heartbeat (#145/#148) with the opt-in `StallAction` kill-and-retry (#173),
  `MtpWorkerFactory.OnBeforeKill` for dump capture before a forced kill (#147), per-worker RSS
  sampling (#149), and `Supervisor.Snapshot()` for cancelled runs (#150). On the Wolverine side,
  wolverine#4108 retired the shell watchdog this rollout had needed — the supervisor now reports
  what the watchdog used to infer.
- **Live progress for foreign workers** (#195, 0.9.1): a supervised xUnit run's console card no
  longer sits at `0 / N` for its whole duration — the supervisor forwards per-test updates for
  workers that publish nothing themselves.
- **The Wolverine-side items** (attacking CIAzureServiceBus and the broker tier, per-lane
  emulators, the `Flaky`-tag removal, scoping Marten's sibling databases) remain Wolverine's
  backlog and are tracked there, not here.
- **Wolverine's local dev note still applies:** the user-level NuGet config maps `Bobcat.*` to a
  folder feed (`~/code/bobcat/artifacts/local-feed`) via packageSourceMapping, exclusively — keep
  it packed at the referenced version, or remove the mapping to restore from nuget.org.
