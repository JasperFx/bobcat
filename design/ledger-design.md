# The test ledger — design of record

Decision of record 2026-08-27, issues #44 (layer 2) and #142 (the ledger both its layers 4–5
wait on); re-keyed and moved to a CI artifact by default on 2026-10-18 (#730, from the #565
rollout). Implementation: `src/Bobcat/Ledger/` (`TestLedger`, `LedgerKeys`),
`src/Bobcat.Supervisor/SupervisorLedger.cs`; properties pinned by `TestLedgerTests` and
`LedgerFeedTests`.

## One store, not two — and its three consumers

The decision from #56 stands: the failure-class ledger #44 deferred and the duration-trend
store #56/#142 deferred are **the same file**, because splitting them means two formats, two
merge strategies, and two files to keep. What settled the build order is the third
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

One `LedgerRun` per (test, run): run id + the run's own timestamp, the test's key (below), its
uid and display name, outcome, attempt count, total and first-attempt
milliseconds (**null when unmeasured, never zero** — the `RunTiming.Unmeasured` rule), the
failure *type name* of the first failing attempt (a name, because out of process a name is all
there ever is — the same reason `FailureSignature` matches on names), the `DispositionKind`
name that preceded a recovery, and whether the attempts were the stall escalation's doing
(#173 — a wedge is not a flake, and stall-induced entries never feed hint evidence).

Outcome and cleared-by travel as **strings**, deliberately: the file is a stored artifact
read by future versions of the tool, and an unrecognized word must degrade to "not understood",
not to a deserialization crash.

The feed is `SupervisorLedger.From(SupervisorResults, runId, at)`. It takes `runId` and `at` from
the caller, because those are the *run's* facts and the fold must never read a clock. Every
attempt survives a supervised run, so a pass-on-retry records both the failure class and what
cleared it.

## The key is the display name (#730)

The ledger was keyed by uid until #730, and the #565 rollout found that useless across machines:
**xUnit v3's uid hashes the assembly path**, so a ledger recorded on a laptop, another CI runner,
another configuration or another TFM matched **0** tests. All four repositories in the rollout hit
it; Marten matched 2521 of 2525 once it re-keyed on display name.

- **A test is keyed by its display name** (`LedgerKeys.For`, one rule for both ends of the join).
  A projected spec's display name is as stable as any other test's.
- **Two tests sharing a display name in one run fall back to their uids**, both of them. Merging
  them would fold two tests' durations into one median, which is worse than not knowing. The
  collision is recorded rather than hidden: such a test is written with its display name beside
  its uid key, and `SharedDisplayNames` lists the names. `Record` applies the same rule to a batch
  that did not use `LedgerKeys` — two uids under one key in one run are separated.
- **`KnownDurations()` is keyed the same way, and `WorkPlan` joins on it**: each discovered test
  is looked up by its ledger key (computed over the plan's tests with the same `LedgerKeys`), then
  by uid, so a hand-built uid dictionary still works. #663's matched-count report counts that join.
- **A collapsed theory is keyed by its discovery name (#747).** Its rows report under one uid
  (#649), each with its own display name, so the fold's last outcome named whichever row decided it
  — the first row on one version, the failing row when one failed — while discovery, and so the
  planner's lookup, names the method. The supervisor reports such a test under the name discovery
  gave it, so the ledger key is the one `WorkPlan` looks up, and it does not move between runs.
- **The uid is not kept on disk** for a test keyed by display name. It is the per-machine hash the
  key exists to get away from; a ledger read back carries the key in `Uid` for such a test.
- **Old files are re-keyed, not discarded.** A version 1 or 2 file (keyed by uid) loads with each
  observation re-keyed by display name — one test's uids from several machines fold into one —
  except display names two uids carried in the same run, which were two tests and stay keyed by
  uid. It is written back as version 3. Re-keying is a pure function of the file, so it keeps the
  fold deterministic.
- A renamed test is a new test. The old name keeps its entries until
  `PruneTestsNotSeenSince` removes them — the same as a deleted test.

## The merge strategy IS the design

\#142's own words: format and keying are choosable in an afternoon; **merge strategy is what
decides whether people keep the file or delete it in annoyance.** Concurrent CI appends to a
committed file are a conflict generator, so the design removes the concept of a conflicting
edit rather than arbitrating one:

- The ledger's state is a **grow-only set of per-(test, run) observations** plus a
  deterministic prune. `Record` and `Merge` are commutative, associative and idempotent —
  union by (key, run id), newest `MaxRunsPerTest` kept, ordered by the observations' own
  stamps.
- Serialization is **canonical**: tests sorted by key, runs newest first, invariant
  formatting, durations as integral milliseconds. Since #698 it is also **compact**:
  unindented, the key written once per test, and the display name written once per test only
  where it differs from the key (a test keyed by uid; the newest observation's) rather than on
  every observation. Schema version 3 since #730 (version 2 had the same shape keyed by uid).
  Older files still load and are written back as version 3; a version newer than the reader is
  refused by name. The same observations produce
  **byte-identical files** whoever folds them, in whatever order, however many times.
- A git conflict between two independently-updated ledgers therefore always resolves the same
  way: load both sides, `Merge`, save. No run artifacts needed, no judgement calls, either
  side can do it, and doing it twice is harmless. A custom git merge driver could automate
  exactly that later; it is not required for the file to be livable.
- Retention disagreements keep the retention of the ledger **merged into** (#698).
  Keeping the larger, as the first cut did, meant one artifact written at the old default 20 grew a
  committed file kept at 3 for good (lesson 14 of `critter-stack-supervisor-rollout.md`; Weasel
  worked around it with `Record`). The committed file's retention is the one somebody chose;
  to change it, merge the ledger into `TestLedger.Empty(n)`, or load it with
  `TestLedger.Load(path, n)`, which does exactly that for an existing file (#746; left out, the
  file's own retention stands). `Merge` is therefore commutative
  between ledgers of the same retention, which is the git-conflict case: both sides of one file.

## Aging is deterministic and clock-free

Two mechanisms, both explicit:

- **Per test**: the newest `MaxRunsPerTest` observations survive, ordered by their own stamps.
  The default is **5** (`TestLedger.DefaultMaxRunsPerTest`, lowered from 20 by #730): enough for
  a median and a trend (`Trends()` asks for 4 measured runs by default), and small enough for an
  artifact uploaded every run. Run-count-based rather than time-based so the fold never asks what time
  it is — half of what makes it deterministic.
- **Per suite**: a test that was renamed or deleted keeps its entries until
  `PruneTestsNotSeenSince(cutoff)` is called — the caller supplies the clock, typically the
  same maintenance moment that runs the fold.

## Derived, never primary — and what that means for CI collection

\#142 flagged "how test results are collected in CI may need revisiting" as the same problem
seen from the other end. The answer here: **the primary record of a run is its report
artifact** — the supervisor's `RunReport.ToJson`, the runner's suite JSON, and the NDJSON
archive the run console keeps (in [Stoat](https://github.com/JasperFx/stoat) since the split). The ledger is a *compaction* of runs, and advisory everywhere it is consumed:
a stale or absent ledger degrades lane balancing and trend fidelity, never correctness.

That is what makes **any collection topology safe**, because the fold is total and
order-free.

**The default is a CI artifact, not a committed file (#730).** Each run downloads the previous
run's ledger, balances on `KnownDurations()`, folds its own observations in, and uploads it again
(the worked GitHub Actions steps are in `docs/parallel-ready-suites.md`, "Durations from the last
run"). The rollout's lesson was that a committed ledger is a commit a test run has to make, and a
conflict generator once several branches make it — while the thing it buys, durations on a
laptop's very first run, is worth little next to a CI run that balances from the second run on.

**Committing it is opt-in**, for a repository that does want durations on a laptop's first run:

- a nightly single-writer job that folds the day's runs and commits (one writer, zero conflicts by
  construction);
- a per-PR local fold committed alongside the change;
- two of those racing — the loser's conflict resolves mechanically by `Merge`;
- or no automation at all, with a developer folding when the balancing feels off.

Nothing in `Supervisor.Run` reads or writes the ledger implicitly. Recording is a caller's line
of code — extract observations, `Record`, `Save` — because writing a file into someone's checkout
or artifact store is not a side effect a test run should ever have on its own.

## Proposals, never policies — the #44 fork stays closed

`ProposeHints()` emits the evidence and the attribute text:

```
[ClearsOnRetry(typeof(TimeoutException), Because = "cleared by retry 9 time(s) in the ledger")]
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

- **A `bobcat ledger` command** (fold a directory of artifacts / resolve a conflict / print
  proposals and trends). The API is the design; the CLI is packaging, and it belongs in the
  `bobcat` tool once the artifact-directory convention settles. Note the ledger is Bobcat's to
  keep: it folds *artifacts on disk*, so it remembers nothing across a process and does not
  follow the console to Stoat.
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
