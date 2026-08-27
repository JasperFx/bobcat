# The committed test ledger — design of record

Decision of record 2026-08-27, issues #44 (layer 2) and #142 (the ledger both its layers 4–5
wait on). Implementation: `src/Bobcat/Ledger/` (`TestLedger`, `LedgerRuns`),
`src/Bobcat.Supervisor/SupervisorLedger.cs`; properties pinned by `TestLedgerTests` and
`LedgerFeedTests`.

## One store, not two — and its three consumers

The decision from #56 stands: the failure-class ledger #44 deferred and the duration-trend
store #56/#142 deferred are **the same file**, because splitting them means two formats, two
merge strategies, and two things to keep committed. What settled the build order is the third
consumer, which is not a report at all:

1. **The scheduler.** `Supervisor.KnownTestDurations` feeds `WorkPlan`'s
   longest-processing-time-first balancer. Without history, a first run balances by test count
   — measured on Wolverine's `PersistenceTests`, count-balanced lanes finished at 101.5s and
   11.4s. `ledger.KnownDurations()` is that feed, available from the first pass of every run.
   This is what makes the ledger infrastructure rather than a reporting nicety.
2. **Recovery-hint proposals** (#44 layer 2). `ledger.ProposeHints()` turns recorded failure
   classes and what cleared them into attribute text — see "proposals, never policies" below.
3. **Duration trends** (#142 layer 5). `ledger.Trends()` names the test that quietly grew from
   2s to 40s — invisible in any single run, obvious across the retained runs.

## The unit of record

One `LedgerRun` per (test, run): run id + the run's own timestamp, the `{Feature}/{Scenario}`
uid everything else already keys on, outcome, attempt count, total and first-attempt
milliseconds (**null when unmeasured, never zero** — the `RunTiming.Unmeasured` rule), the
failure *type name* of the first failing attempt (a name, because out of process a name is all
there ever is — the same reason `FailureSignature` matches on names), the `DispositionKind`
name that preceded a recovery, and whether the attempts were the stall escalation's doing
(#173 — a wedge is not a flake, and stall-induced entries never feed hint evidence).

Outcome and cleared-by travel as **strings**, deliberately: the file is a committed artifact
read by future versions of the tool, and an unrecognized word must degrade to "not understood",
not to a deserialization crash.

Two feeds produce observations; both take `runId` and `at` from the caller because those are
the *run's* facts and the fold must never read a clock:

- `SupervisorLedger.From(SupervisorResults, runId, at)` — the rich one: every attempt survives
  a supervised run, so a pass-on-retry records both the failure class and what cleared it.
- `LedgerRuns.From(SuiteResults, runId, at)` — the in-process one, with two honest gaps:
  durations are the #141 bracket wall clock (absent for results with no bracket), and only the
  final attempt's `ExecutionResults` survives in-process, so a pass-on-retry knows how it was
  cleared but not what it recovered from.

## The merge strategy IS the design

\#142's own words: format and keying are choosable in an afternoon; **merge strategy is what
decides whether people keep the file or delete it in annoyance.** Concurrent CI appends to a
committed file are a conflict generator, so the design removes the concept of a conflicting
edit rather than arbitrating one:

- The ledger's state is a **grow-only set of per-(test, run) observations** plus a
  deterministic prune. `Record` and `Merge` are commutative, associative and idempotent —
  union by (uid, run id), newest `MaxRunsPerTest` kept, ordered by the observations' own
  stamps.
- Serialization is **canonical**: tests sorted by uid, runs newest first, invariant
  formatting, durations as integral milliseconds. The same observations produce
  **byte-identical files** whoever folds them, in whatever order, however many times.
- A git conflict between two independently-updated ledgers therefore always resolves the same
  way: load both sides, `Merge`, save. No run artifacts needed, no judgement calls, either
  side can do it, and doing it twice is harmless. A custom git merge driver could automate
  exactly that later; it is not required for the file to be livable.
- Retention disagreements merge to the **larger** `MaxRunsPerTest` — shrinking someone's
  history because the other side was configured smaller would silently discard a choice.

## Aging is deterministic and clock-free

Two mechanisms, both explicit:

- **Per test**: the newest `MaxRunsPerTest` observations (default 20) survive, ordered by
  their own stamps. Run-count-based rather than time-based so the fold never asks what time
  it is — half of what makes it deterministic.
- **Per suite**: a test that was renamed or deleted keeps its entries until
  `PruneTestsNotSeenSince(cutoff)` is called — the caller supplies the clock, typically the
  same maintenance moment that runs the fold.

## Derived, never primary — and what that means for CI collection

\#142 flagged "how test results are collected in CI may need revisiting" as the same problem
seen from the other end. The answer here: **the primary record of a run is its report
artifact** — the supervisor's `RunReport.ToJson`, the runner's suite JSON, the monitor's
NDJSON archives. The ledger is a *compaction* of runs, and advisory everywhere it is consumed:
a stale or absent ledger degrades lane balancing and trend fidelity, never correctness.

That is what makes **any collection topology safe**, because the fold is total and
order-free:

- a nightly single-writer job that folds the day's runs and commits (recommended — one writer,
  zero conflicts by construction);
- a per-PR local fold committed alongside the change;
- two of those racing — the loser's conflict resolves mechanically by `Merge`;
- or no automation at all, with a developer folding when the balancing feels off.

Nothing in `BobcatRunner.RunAll` or `Supervisor.Run` writes the ledger implicitly. Recording
is a caller's line of code — extract observations, `Record`, `Save` — because committing a
file to someone's repository is not a side effect a test run should ever have on its own.

## Proposals, never policies — the #44 fork stays closed

`ProposeHints()` emits the evidence and the attribute text:

```
[ClearsOnRetry(typeof(TimeoutException), Because = "cleared by retry 9 time(s) in the committed ledger")]
```

**A human accepts a proposal by writing it into the code.** Nothing in Bobcat reads proposals
back into an `IFailurePolicy`, and nothing ever should: a policy that silently learns "just
retry this" is exactly how red gets laundered into green with nobody deciding to. The
counterweight is proposed on the same terms — a class that was retried and never once
recovered earns a `[NeverRecovers]` suggestion. One lucky retry is an anecdote:
`minOccurrences` (default 3) gates both directions. A recycle-shaped recovery proposes
`[ClearsOnRecycle]` with the resource list left for the human — the ledger records the
disposition kind, not which brokers were bounced, and naming them is precisely the judgement
the fork reserves.

## Deliberately not built (yet)

- **A `dotnet bobcat ledger` command** (fold a directory of artifacts / resolve a conflict /
  print proposals and trends). The API is the design; the CLI is packaging, and it belongs in
  the console tool once the artifact-directory convention settles.
- **Automatic wiring of `KnownTestDurations`** — the loop closes today in one line
  (`supervisor.KnownTestDurations = TestLedger.Load(path).KnownDurations()`), and implicit
  file reads from a library are the same kind of side effect as implicit writes.
- **Sleep-shaped duration heuristics** (#142 item 4). The cross-run variance it needs now
  exists, but its false-positive question is still open, and `[SlowByDesign(Because = ...)]`
  belongs in `JasperFx.Testing` per the #63 precedent — a cross-repo decision, not a layer to
  smuggle in here.
- **Cross-repo artifact shipping** (runs on other machines feeding one ledger). The merge
  makes it safe whenever it is wanted; wanting it is Stoat-shaped territory and stays out of
  an MIT repo's scope.
