# Plan — closing the open issues, 2026-09-15

Scope: every open issue except #109 (a Rider plugin of our own, unscheduled by decision). That
leaves **#297, #298, #300** and the tracking issue **#257**. All three work items are phase 3–4 of
`docs/event-model-canvas-design.md`; phases 0–2 and 5 shipped on 2026-09-14/15.

`main` is at `89e6a11`. Local main was 26 commits behind at the start of this session and has been
fast-forwarded; the working tree is clean.

## What the research changed about the issues as written

1. **Nothing is blocked upstream any more.** Every dependency the issues name is in the pinned set:
   JasperFx 2.69.3 carries `ConsumedEvents` / `ReadsFrom` / `Chapter` (jasperfx#824) and
   `EventModelDescriptor.Links` (jasperfx#823); JasperFx.Events 2.69.3 carries
   `ProjectionEventModelSource` (jasperfx#825), which stamps `Pattern = View`, `ProjectionTypes`,
   `ReadModelTypes` and `ConsumedEvents` from the projection's real apply set; WolverineFx 6.38.0
   splits reads from writes (wolverine#4419) and fixes the `ServiceName` split (GH-4448). Marten
   9.36.0 and Fisher 1.10.0 register their store source from `AddMarten` / `AddFisher` with no
   consumer wiring. PR #309 landed the bump on 2026-09-15 with `samples/BankAccountES` 16/16 on
   both legs.
2. **#300's "store rung arrives" is already happening in the sample.** The two-models failure the
   bump branch hit *was* the store source appearing; the sample now assembles four sources
   (specs, overlay, Wolverine chains, store) and its `EventModel.feature` still says "three". The
   acceptance work is to assert the fourth, not to make it arrive.
3. **The acceptance text in #297 and #300 names a read model the sample does not have.** Both say
   `AccountBalance`. The sample's read models are `Account`, `Client` and `AccountTransactions`
   (inline `Snapshot<T>` registrations in `Program.cs`), so the store-derived View slices are named
   `Account`, `Client`, `AccountTransactions`. Write the scenarios against `Account`.
4. **The sample has no View-pattern scenario.** Every scenario has a `When … is received` or
   `is posted to`, so the generator derives `Command` for all of them and `{event}`-on-`Given`
   stays unstamped everywhere. #297's Bobcat-side acceptance and #300's "one slice, not two"
   assertion both need a View scenario added to the sample — a `Given AccountOpened occurred …
   Then the Account read model contains` scenario tagged `@slice:Account` — so the spec-declared
   slice and the store-derived slice share a name and merge. That single scenario is the pivot
   both issues turn on, which is why they are one branch below.
5. **#298 has an undecided question the issue itself flags**: chapter *order*. The descriptor
   carries a chapter name and nothing orders chapters. The canvas does not reorder slices
   (declaration order is the producer's statement), so a chapter band is one per contiguous run
   of same-chapter slices. Recommendation: **do not add ordering** — bands follow slice
   declaration order, the import writes slices chapter by chapter so the emlang board's order
   survives naturally, and the import report warns when a chapter is split. Raise an upstream
   `ChapterOrder` role only if a real board comes out interleaved.

## Sequencing

Three branches, in this order. The first is the smallest and de-risks the other two by proving
the store rung merges by name before anything is built on top of it.

### 1. `gh-300-store-rung` — #300, the store rung acceptance (small)

All in `samples/BankAccountES`, no library change.

- Add a View scenario to a new `Features/AccountView.feature` (fixture `AccountViewFixture :
  CritterStackFixture`), tagged `@slice:Account`, arranging with `Given AccountOpened occurred`
  and `Given FundsDeposited occurred` and asserting `Then the Account read model contains`. It
  runs as a real spec on both store legs — the arrange appends, the inline snapshot projects.
- `EventModel.feature`: retitle the fold scenario to four sources and add one scenario:
  - exactly one slice named `Account`;
  - `ProjectionTypes` / `ReadModelTypes` / `ConsumedEvents` claimed by `Derived`;
  - `Specifications` claimed by `Declared`;
  - no `SourceDisagreement` hotspot on `Account`;
  - a link of kind `EventConsumed` from the `OpenAccount` slice to the `Account` slice via
    `AccountOpened` (the `Links` payoff, asserted once end to end).
- `EventModelFixture.cs` gains the two steps that do not exist yet: "the slice's `{role}` is
  claimed by …" already exists; add "there is exactly one slice named X" and "there is an
  `{kind}` link from X to Y via Z" (the fixture already assembles through
  `EventModelDiscovery.Assemble`, so `Model.Links` is one property away).
- Make each new assertion fail once before trusting it (rename the slice tag to `Accounts` and
  watch "one slice, not two" go red), per the handoff method note.
- Verify on **both** legs locally: Marten (Postgres 5433, database `bank_account`) and Fisher.
  `samples.yml` runs only on `main`, so a PR's green is not the gate here.

Risk: the store names the slice after the document type's short `Name`, and the generator names a
`{readmodel}` slice from the `@slice:` tag, not from the type. If the tag is not spelled exactly
`Account` the merge yields two stickies — which is the exact failure the scenario exists to catch,
so that is the first thing to see red.

### 2. `gh-297-consumed-events` — #297, stamp what a slice reads (medium)

Generator first, then the two declared sources, then the scaffolder.

- **`EventModelEmitter`** (`src/Bobcat.Generators/EventModelEmitter.cs`): `roleOf` currently maps
  `{event}`-on-`Given` to `Unstamped`. Change it to a per-slice decision: collect arranged events
  into a new `SliceModel.ConsumedEvents`; at emit time, if `patternOf(slice)` is `View`, emit
  `ConsumedEvents = …` in `emitSlice`; if `Command`, discard (the #259 demotion stands). Because
  arrangements are expanded before matching and `Given events for {aggregate}` names events in a
  table cell, only the `{event}` capture path needs the change — the table path stays uncaptured
  and therefore unstamped, which #297's text over-promises; note that limit in the CLAUDE.md
  update rather than parse table cells at compile time. Code-first (`CodeFirstSpecs`) mirrors it:
  `GivenEvents<T>(…)` argument types on a View spec become `ConsumedEvents`.
- **`EventModelDescriptorTests`**: both halves positive — View scenario stamps, Command scenario
  does not; a scenario that is both (has a `When {command}`) is Command.
- **Curated YAML**: `CuratedSlice.ConsumedEvents` / `ReadsFrom` (`List<string>`), carried by
  `CuratedModelMapper` (`Declared`, through `types(...)` like `ReadModels`) and written by
  `CuratedModelWriter` only when non-empty; round-trip test byte-identical.
- **`EmlangImport.viewSlice`**: the `e:` steps since the last `v:` or chapter start become the
  View slice's `consumedEvents`; report line `View slice 'X' consumes N event(s): …`. Fixture
  `e: A / e: B / v: V / e: C / v: W` → `V=[A,B]`, `W=[C]`.
- **`SliceScaffolder`**: `ViewSourcesFor` today derives a View slice's arranged events by
  searching the model; prefer the slice's own `ConsumedEvents` when present, fall back to the
  search otherwise. Existing scaffolder tests must not change output for a model with no
  `consumedEvents`.
- **Docs**: CLAUDE.md "Named arrangements" and "Event Modeling slice tags" — the demotion is now
  "Command slices only"; `docs/event-model-canvas-design.md` decision 3 is already the rationale.
- Acceptance on the sample: the `Account` View scenario from branch 1 now yields a spec-side
  `ConsumedEvents = [AccountOpened, FundsDeposited]`; with the store side saying the same thing,
  assert the role is claimed by **both** provenances with no disagreement. If the store's apply
  set is wider than the spec's (it will be — `FundsWithdrawn`, `AccountFrozen`), the type-list
  merge is a union, not a disagreement; the scenario should say so explicitly.

### 3. `gh-298-chapters` — #298, chapters through import, tag, and canvas (medium-large)

Split into a Bobcat half and a package half; they can land in either order because the canvas
tolerates a missing `chapter` and the descriptor tolerates a canvas that ignores it.

Bobcat half:
- `EmlangImport`: `Chapter = chapter.Name` on every slice from `commandSlice` / `viewSlice`; a
  slice folded from a second chapter keeps the first and the report says so.
- `CuratedSlice.Chapter`; mapper and writer; scaffolder writes it when the source had one.
- `GeneratorSliceTags` + `Bobcat.Runtime.SliceTags`: `@chapter:<name>`, same shape as `@domain:`;
  `SliceTagParsingAgreementTests` extended; `emitSlice` writes `Chapter = …`.
- `Bobcat.Console.Specs/Features/EventModel.feature`: a pushed document with chapters round-trips
  through `GET /api/event-model` (the store normalizes through the typed descriptor, so this
  should pass with no console change — the scenario pins that).

Package half (`@jasperfx/event-model-vue`, then a version bump and a **manual npm dispatch**):
- `types.ts`: `chapter?: string | null` on the slice mirror, pinned in `types.spec.ts`.
- `layout.ts`: chapter bands as a new pure output — one band per contiguous run of same-chapter
  slices, above the slice columns, spanning their x-range. `layout.spec.ts` pins coordinates on a
  fixture with two chapters and one split chapter.
- `EventModelView.vue`: render the band, label once, legible at `overview` LOD; click → focus.
  `focus.ts` already anticipates a `chapter:` rung (line 20 and `viewportFromQuery` tests) — the
  hierarchy becomes model → chapter → slice → spec.
- `filters.ts` / filter bar: chapter chips beside domain chips, `chaptersOf()` mirroring
  `domainsOf()`; "N of M" count unchanged.
- CritterWatch consumes the package by pin; bump there after publishing.

Decision needed before the package half: chapter ordering (see finding 5). Recommended answer is
declaration order with no new role.

### 4. #257 — close the tracking issue

After 1–3 merge, #257's Bobcat-side children are all done. Its two remaining checkboxes
(CritterStackSamples#16, #17) are another repo's and are open there. Post a closing comment that
lists what shipped per phase, points the two samples issues at their own repo, and close it. The
`event-modeling-wave-2` Stoat plan should be marked complete in the same pass.

## Order of operations for a session

1. Branch 1 (#300). Small, and it proves the merge-by-name assumption the other two rest on.
2. Branch 2 (#297). Generator + declared sources + scaffolder; extends branch 1's scenario.
3. Branch 3 (#298), Bobcat half, then package half with the ordering decision made.
4. Close #257.

Each branch: PR with merge commit, green on the six workflows, and a local run of
`samples/BankAccountES` on both legs because `samples.yml` does not run on PR branches. Release
0.21.0 after 1–2 (both change the generated descriptor, which consumers' Event Model pages read);
the package version bump for #298 needs the manual `workflow_dispatch` or it is not published.

## Environment reminders (from the 2026-09-14 handoffs)

- `docker compose up -d` at the repo root: Postgres 5445, RabbitMQ 5683. Do not run it from a
  scratchpad clone — the shared stack already holds the ports.
- `samples/BankAccountES` uses Postgres **5433** with a `bank_account` database; missing database
  reads as `0/0 scenarios passed`.
- `MSBUILDDISABLENODEREUSE=1`; never unscoped `pkill`.
- `dotnet test --filter` is ignored under MTP — use `-- --filter-class` / `--filter-method`.
