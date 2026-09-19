# Event Modeling: where we are, and what needs a human

**2026-09-19.** Written after a day of finding scaffolder defects by *reading generated code*, which
is the method this plan mostly proposes repeating on surfaces nobody has read yet.

> Spans three repos — Bobcat, Stoat, CritterStackSamples — and lives here because Bobcat is where
> most of the reading is. Committed rather than left in a working copy for the reason
> `.claude/handoffs/2026-09-14-bobcat-event-model-wave.md` gives: a design of record that exists only
> as an untracked file in one clone is one nobody else can act on.

## Where Event Modeling actually is

**The generation path is proven end to end and the evidence is a repo, not an opinion.**
CritterCrush regenerates from a curated model on Bobcat 0.26.3 with **zero structural edits** —
nothing the scaffolder emits has to be replaced — and runs **88 specs green**. That claim is
falsifiable and was falsified repeatedly today: every fix was verified by regenerating and running,
and two were caught *because* the suite went red.

What the day actually demonstrated is worth stating, because it decides the plan below. **Ten
defects, all found by a human-style read of generated output. None by a test.** #355 (a dead `Apply`
indistinguishable from live ones), #349 (two views keyed identically, compiles and runs), #351 (a
projection that cannot register), #357 (a Marten type in store-neutral code), #360 (a creating slice
asserting nothing about the identity it minted). The scaffolding suite was green through all of them,
because it asserts **text**.

**So the bottleneck is not correctness-by-testing. It is that nobody has read most of the surface.**

## What is definitely fine

- The curated format and its validation — exercised hard today, and every gap found was a *missing*
  option rather than a wrong one.
- The scaffolder's slice, aggregate, projection and spec emission — read line by line, four rounds.
- `ai-skills` on the shapes CritterCrush uses. Now documents `refusedWith:`, `startsStream:`, and
  the spec-ownership manifest, which had **no documentation at all** before yesterday.

## What needs a manual pass, and why each

### 1. Bobcat docs — the highest-value read, and partly known-stale

17 files. Evidence they have drifted:

- **Four docs still describe the console as part of Bobcat** (`command-line.md`, `monitor-design.md`,
  `ledger-design.md`, `sample-wiring.md`) — it moved to Stoat in the split. These were touched on
  9/18 by bulk edits, so their git dates look current and are not a signal.
- **Nothing in `docs/` mentions `refusedWith`, `startsStream`, or `[EmptyResponse]`**; one file
  mentions `defaults:`. Three releases of format and scaffolder change are undocumented.
- The curated format is documented in **ai-skills**, not here — so the two can drift, and today they
  had: the skill's `then:` table was missing two of five options for three weeks.

**The read to do:** each doc, against the behaviour it claims, running the commands it gives.
`getting-started.md` and `command-line.md` first — they are what a new user hits, and
`command-line.md` is both console-stale and scaffolder-relevant.

### 2. Bobcat public API — 75 public types across three packages

`Bobcat.EventModel` 33, `Bobcat.EventModel.Scaffolding` 27, `Bobcat.CritterStack` 15.

This has never had a deliberate pass. Specific things to decide rather than discover later:

- **What is genuinely public vs incidentally public.** `SpecOwnershipPlan`, `SliceScaffolder`,
  `MarkerInterface`, `SpecFixture` are public because the samples repo's runner calls them — that is
  one consumer, and it pins the API for everyone.
- **`ScaffoldCompilesTests` asserts text despite its name.** The API's own test suite cannot tell a
  registrable projection from an unregistrable one; that is how #351 shipped. Any API review should
  decide which suites are allowed to assert text.
- Naming consistency across the three, now that `Bobcat.Cli` → `Bobcat.Console` restored one id and
  the console left.

### 3. Stoat UI — thinner than its logic

12 Vue files, **22 test files**. Stores, messages, composables and `lib` are well covered; **views
are not**: `AgentsView`, `PlanView`, `PlansView`, `ReadyBoardView`, `RunView` have no tests, and
`LaneStrip` / `NodeKindIcon` none either. `DashboardView` and `EventModelPage` do.

I corrected myself twice while measuring this — a bad glob said "no tests", a truncated `head` said
"five". **The numbers above are from a complete `find`.** Worth saying because a plan built on the
first impression would have been wrong in an expensive direction.

The manual pass here is **not** "add view tests" — it is *look at the thing running*. Five open
Stoat issues are UI/UX-shaped already (#22 wrong port, #20 silent color rewrite, #21 silent deletion
of API-authored changes), and two of those are **silent data loss**, which is exactly what a human
clicking around finds and a unit test does not.

### 4. The Event Model viewer specifically

`EventModelPage.vue` (344 lines) renders `@jasperfx/event-model-vue`, which is now a separate npm
package. Nobody has reviewed how a CritterCrush-shaped model *looks* on it — 19 slices, two
chapters, 52 scenarios. That is the blog-post artifact and the thing a prospective user judges.

## Suggested order

1. **Bobcat `getting-started.md` + `command-line.md`**, run as written. Highest chance of being
   wrong, lowest cost to check, worst first impression if wrong.
2. **Load CritterCrush's model into the Stoat viewer and look at it.** It is the only real model we
   have; the viewer has never been judged against one.
3. **The remaining Bobcat docs**, console-stale four first.
4. **Bobcat public API**, once the docs tell us what we *meant* to expose.
5. **Stoat UI walk-through**, with #20/#21/#22 as the starting hypotheses.

## What I would not do yet

- Writing view tests for Stoat before anyone has looked at the views. The defects found today were
  all "this compiles, runs, and is wrong" — tests written blind would pin the current behaviour,
  including whatever is wrong about it.
- A Bobcat API freeze before the docs pass. The docs are what tell us which surface was ever
  intended to be public.
