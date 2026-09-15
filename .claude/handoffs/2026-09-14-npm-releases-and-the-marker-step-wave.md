# Handoff — npm releases, stream rows, marker steps, 2026-09-14 (evening)

`main` at **422b3e2**, latest tag **v0.19.0**, **no open PRs**, working tree clean.
Open issues: **109, 257, 295, 297, 298, 300**. Continues
[`2026-09-14-bobcat-event-model-wave.md`](2026-09-14-bobcat-event-model-wave.md) from the same day.

**0.20.0 shipped** after this note was first written — tag `v0.20.0` at `e417df5`, run
34915043649 green, all **14** packages verified restorable on the flat container within ~4 minutes
(no wave lag). It carries more than the merges listed below: `v0.19.0` predates the whole of
2026-09-14, so #294 and #296 are in it too. CritterWatch was deliberately **not** released in the
same pass.

## What shipped

| | |
|---|---|
| **npm 0.9.0** | Published at last. It had been bumped by #296 on 9/12 and **never dispatched** — the publish workflow is `workflow_dispatch`-only, so a merged bump proves nothing. CritterWatch#1244 and #1247 had both been filed as blocked on "nothing newer than 0.8.0 is published". |
| **#299** → PR #306, `61d4b2e` | Stream rows: the EventStream lane splits into one row per aggregate. Package **0.10.0**, also published. |
| **#304 + #305** → PR #307, `af515fa` | A marker comment renders as a step, and the work under it attributes itself. Both issues closed. |
| **#308** → `f4d60fa` | #258 gap 4: an HTTP act settles its slice's `TriggerKind` and route. |
| **#258** | Closed as answered, with its gap list dispositioned. Gap 9 filed where the work lives: CritterStackSamples#19. |
| **#109** | Reframed: a Bobcat Rider plugin **of our own**, unscheduled. |
| CritterWatch #1248, #1249 | The consuming pin, 0.8.0 → 0.9.0 → 0.10.0. Both merged. |

`main` is green on all six workflows after each merge, `samples` included — and `samples` does **not**
run on PR branches, so a PR's green is not the whole gate here.

## Decisions of record

- **#305 needs no precedence rule.** A comment and a `[BobcatStep]` attribute never occupy the same
  row: the comment is the sentence, the helper renders underneath it with its own keyword and
  verdict. A declared row reports the work observed inside it and **nothing else** — a region with
  no decorated helper stays blank rather than borrowing a number from its neighbours.
- **Attribution is a compile-time fact.** The generator computes which comment a call sits under
  from the call site's line, because an interceptor is generated per call site. At runtime it would
  be a stack trace and a hope, and `docs/marker-steps.md`'s "declared is not executed" rests on the
  difference.
- **Stream rows need two aggregates in view.** One stream is not a comparison, so a one-aggregate
  model is coordinate-identical to before — which is also why filtering a 106-slice model down to
  one aggregate collapses the lane instead of leaving empty rows. A `Message` and an event whose
  slice writes no aggregate share one trailing unlabelled row.
- **Gherkin derives `TriggerKind.Http` and nothing else.** `is posted to` is the grammar's own
  sentence; `is received` dispatches ordinary commands too, so MessageHandler stays (c).
- **#109: our own Rider plugin, unscheduled.** Retires the upstream ladder, the 2026-09-18 threshold
  and any Reqnroll dependency. Reqnroll.Rider#92 stays open — a merge makes the work *less* urgent.
  Carried to stoat#9 decision 11 (superseded text folded into a `<details>`) and to the canonical
  note, CritterWatch `6afb1192`.

## Four traps, three of them expensive

1. **A version bump is not a published package.** `@jasperfx/event-model-vue` publishes only on
   manual dispatch. 0.9.0 sat unpublished for two days and blocked two CritterWatch issues while
   reading, from `main`, as shipped. Check the registry, not `npm view` (it serves a local cache)
   and not `package.json`:
   `curl -s https://registry.npmjs.org/@jasperfx/event-model-vue | python3 -c "import sys,json;print(list(json.load(sys.stdin)['versions'].keys()))"`.
   The registry also lags a successful run by ~2 minutes.
2. **A `void` `[BobcatStep]` helper had never compiled.** The emitter wrote `return receiver.M()`,
   which is CS0127 on void — in the *consumer's* build, in a file they cannot open. It survived to
   0.19.0 because every test read the generated **text** and **no project in this repo compiled an
   interceptor**: the opt-in lives in `buildTransitive`, which a package consumer gets and a
   ProjectReference does not. `Bobcat.Acceptance.Tests` now opts in and holds the only tests where
   an interceptor intercepts. **General rule: a generator test that asserts on emitted text proves
   the text, not the build.**
3. **`dotnet test --filter` is silently ignored under Microsoft.Testing.Platform.** It warns
   `MTP0001: VSTest-specific properties ... will be ignored` and runs *everything*. Harmless when
   the suite is green; badly misleading when you believe you ran three tests and did not.
4. **The wire mirror in `event-model-vue` was wrong, not thin.** `EventModelDescriptor.aggregates`
   was typed as the slices' Aggregate *cards*. Nothing read it, so nothing failed, until a stream
   row needed `appliedEvents`.

## Method note that earned its place

Every new headline assertion was **made to fail on purpose before being trusted** — the generator's
`[-1, 0, 1, 2, -1]` attribution, the end-to-end `[1, 2, 3]`, and the `/wallets/credit` route (broken
to `/credit`, which is what proves the prefix composition is covered rather than incidentally true).
Three for three they did fail, but the point is that "it passed first time" is not evidence until
you have seen the check fail.

## Ready to pick up

- **#295 / #297 / #298 / #300** — the event-modeling notation work, and the next session's
  intended focus. All four are recorded as waiting on the aligned-set bump
  (`bump-aligned-set-2.69.3`), which is recorded as waiting on a Marten release carrying
  marten#5411.

  ⚠️ **That second wait looks stale. Re-test before believing it.** The morning handoff says the
  fix is "**Not in 9.36.0**, the newest release". Git says otherwise: `34bd13dcd` (#5411, 2026-09-13
  13:52) is an ancestor of `baf1fc3f4` ("Release 9.36.0"), six commits back, and is contained in tag
  `V9.35.0`. Marten 9.36.0 — the version the bump branch already pins — therefore **has** the fix.

  What that does *not* settle is the symptom. `samples/BankAccountES` really did go 16/16 → 10/16
  on the bump with `Expected exactly one assembled model, but got [BankAccount, BankAccountES]`, and
  the same note records one already-corrected wrong mechanism for it. So the honest state is: the
  cause was misattributed to an unreleased fix, the failure may still be real, and the cheap first
  move is to re-run that sample on `bump-aligned-set-2.69.3` (Postgres 5433, database
  `bank_account`) rather than to assume either answer.
- **CritterStackSamples#19** (gap 9) — and it is not the one-scenario job #258's closing summary
  implied: `MyAppointmentsProjection` is a scaffold with nine `Apply` methods all throwing.
- **#109** — unscheduled by decision, not by neglect.

## Environment

- **Do not `docker compose up` in a scratchpad clone.** Ports 5445 / 5683 are already held by the
  shared `~/code/bobcat` stack from another session; a second project fails on
  `Bind for 0.0.0.0:5445 failed`. The suites reach the existing stack on those ports anyway.
- `~/code/bobcat` is shared — work from a scratchpad clone and push direct.
- Build with `MSBUILDDISABLENODEREUSE=1`. Never unscoped `pkill -f testhost` / `MSBuild.dll`.
- Merge commits here, not squashes (CritterWatch squashes). Docs and version bumps go direct to
  main. Merges are the user's call, green-gated, never `--auto`.
- **CritterWatch CI is parked** (workflows moved to `/CI`), so a PR there reports no checks at all —
  the local run *is* the gate, and "no checks" is not a stuck PR.
