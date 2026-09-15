# Handoff — Bobcat, Event Model wave, 2026-09-14

`main` at **29914a1**, latest tag **v0.19.0**, **no open PRs**, working tree clean.
Open issues: **109, 257, 258, 295, 297, 298, 299, 300, 304, 305**.

Committed rather than left local on purpose. #303 was filed today because the canvas design of
record existed only as an untracked file in one working copy while five issues cited it — a
handoff left the same way would repeat that exact mistake.

## What shipped today

| | |
|---|---|
| **#294** → PR #301, `1b549f5` | The runner publishes its spec assembly's Event Model half. Nothing outside `Bobcat.Console` had ever PUT to `/api/event-model`, so the per-source merge #268 built had nothing to merge on a real app. |
| **#296** → PR #302, `ed7c6a5` | Canvas navigation: focus with neighbourhood, `data-lod` levels, minimap, viewport in the URL. `layoutEventModel` untouched — all decisions are pure functions in a new `focus.ts`. |
| **#303** → `29914a1` | `docs/event-model-canvas-design.md` committed, plus a sidebar entry under Supervising. |
| **#110** | Closed as delivered. Scope call is the last comment on the issue. |
| **#304, #305** | Filed — the two marker-step leftovers split out of #110. |

Main CI is green on `ed7c6a5`: all six workflows. Both PRs were tested against an older main, so
the combination was verified separately — do that again after any pair of merges.

Together #301 and #302 are the payoff this wave was aimed at: the spec half of the model now
reaches the console, and a 106-slice canvas is navigable. Those were the two halves of "a slice
shows whether it is specified and green".

## Ready to pick up now

- **#295** (draw the cross-slice links) and **#298** (chapters) — both were blocked on the design
  doc that is now committed, and both need **the aligned-set bump below**, since their upstream
  surface (`Links`, `Chapter`) is not in the pinned JasperFx.
- **#297** (stamp `ConsumedEvents` on a View slice) — same bump dependency.
- **#300** (store rung acceptance) — see the bump; it is the same piece of work.
- **#304 / #305** — small, self-contained, no dependencies. Good parallel work.
- **#258** — the equivalence experiment is run and answered; only two small leftovers remain
  (an HTTP trigger kind, and a MyAppointments scenario). Could be closed instead.

## The aligned-set bump — branch pushed, deliberately no PR

Branch **`bump-aligned-set-2.69.3`** at `804979a`. WolverineFx 6.37.0 / Marten 9.36.0 /
Fisher 1.10.0 / Polecat 5.29.0 / JasperFx(+Events, +Events.SourceGenerator) 2.69.3, across `src/`
and `samples/BankAccountES`.

**2.69.3 and not 2.70.0.** Every store in the set is *built against* 2.69.3; nothing is built
against 2.70.0. A floor constrains resolution, not the vtable — the rule is written up at length
in `src/Directory.Packages.props` and `docs/versions.md`.

**Bobcat itself is clean on it**: 1392 passed / 0 failed, identical to the same suite on the
pre-bump pins, zero `TypeLoadException`, and a full rebuild emits no deprecation warnings — only
pre-existing nullability and analyzer noise.

**`samples/BankAccountES` goes 16/16 → 10/16, and that is why there is no PR.** All six failures
are Event Model scenarios, all the same assertion:

```
Expected exactly one assembled model, but got [BankAccount, BankAccountES]
```

**Cause, verified in source after a first wrong guess:** Marten's `MartenProjectionEventModelSource`
(not JasperFx's `ProjectionEventModelSource`). In 9.36.0, with `StoreOptions.EventModelName` null,
it falls straight through to `ProjectionEventModelSource.DefaultModelName`, so the store's model is
named independently of the host's — Wolverine uses `ServiceName` and Bobcat's spec assembly uses
`[assembly: EventModelName]`, both `"BankAccount"` in that sample.

**Already fixed upstream and unreleased.** marten#5405 → PRs #5407 and #5411 (`34bd13dcd`,
"default the Event Model name to the service name"). The fallback is now
`EventModelName ?? JasperFxOptions.ServiceName ?? Default`. **Not in 9.36.0**, the newest release.

So: either wait for a Marten release carrying #5411, or land the bump with a one-line stopgap
(`opts.EventModelName = "BankAccount"` in the sample) that the release then makes redundant. Do not
file this upstream — it is diagnosed and fixed.

## Decisions of record made today

- **#110 is delivered, and Gherkin stays the default authoring surface.** The scaffolder keeps
  emitting `.feature`; C# is *an* alternative. #259 shipping narrowed #110's own argument — an
  arranged event's type is now an `{event}` capture, so a misspelling is BOBCAT011 at build without
  leaving Gherkin. What remains C#-only is IntelliSense, rename refactoring, and #241's column class
  (still a runtime binding): authoring ergonomics, not correctness.
- **Code-first specs are first-class for slice gating.** `EventModelEmitter.Collect(CodeFirstSpecs.SpecInfo, …)`
  applies the same rules as the Gherkin overload — identity `{FeatureTitle}/{ScenarioTitle}`,
  `@slice:`/`@domain:` via `[Scenario(Tags = …)]`, roles, pending-spec hotspots. One asymmetry: no
  trigger label, because there is no `Triggered by` line.
- **Section 2 of the canvas design is now wrong on purpose.** `min(vw/w, vh/h)` is a no-op for a
  scroller that grows to its content (measured: "fit" moved 46% → 48% on 106 slices). #302 fits on
  width alone unless the viewport really scrolls vertically. Left unedited in the document so the
  change reads as a decision rather than a silent rewrite; noted on #303.

## Environment

- `docker compose up -d` in the repo root: **Postgres 5445, RabbitMQ 5683**. Both are required —
  #282's transport-aware reset means an in-memory transport cannot exercise isolation.
- `samples/BankAccountES` points at **Postgres 5433** (Wolverine's compose, *not* Bobcat's 5445) and
  needs a `bank_account` database to exist. Without it the run reports `0/0 scenarios passed`, which
  reads like a discovery failure rather than a missing database. It was created on
  `wolverine-postgresql-1`.
- Build with `MSBUILDDISABLENODEREUSE=1`. Never run unscoped `pkill -f testhost` / `MSBuild.dll` —
  other repos' suites share this machine.
- `~/code/bobcat` is shared with other sessions. Work from a scratchpad clone. Port **5525** is held
  by another session's `bobcat` process; agents used 5599 and 5613 for their own consoles.
- The repo merges PRs with **merge commits**, not squashes. Docs and version bumps go direct to main.
  Merges are the user's call, green-gated, never `--auto`.

## Three method notes that cost real time today

1. **A check that cannot fail is not a check.** Three separate false greens: a stale `obj` that made
   a broken analyzer reference look fine; a *partial* revert (sample only, `src/` still bumped) that
   reproduced a mixed-set failure and looked like a baseline; and a CI wait loop whose SHA filter
   matched nothing, so "none pending" was trivially true. In each case "found nothing" was
   indistinguishable from "nothing wrong". Make the condition require a positive find.
2. **Revert everything for a baseline, not the part you suspect.**
3. **Read the source before naming a cause.** I published a wrong mechanism for the two-models
   failure to #300 and had to correct it. The symptom was real; the inferred cause was not.
