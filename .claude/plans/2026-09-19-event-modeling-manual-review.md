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

### 1. Bobcat docs — a real read, but NOT for the reasons first claimed

> **CORRECTED 2026-09-19, same day.** The first version of this plan said four docs still described
> the console as part of Bobcat, and that three releases of format change were undocumented. **Both
> were wrong**, and both came from grepping for a WORD rather than checking a claim.
>
> - `command-line.md`, `monitor-design.md` and `ledger-design.md` were all updated during the split
>   and say the console moved to Stoat — `command-line.md` carries an explicit note about it.
>   `sample-wiring.md`'s hits are console *logging*, nothing to do with the run console.
> - `refusedWith` / `startsStream` / `[EmptyResponse]` are absent from `docs/` **by design**: no
>   Bobcat doc documents the curated `then:` vocabulary. That reference lives in `ai-skills`, which
>   is where it was fixed. No Bobcat doc carries a canonical grammar step table either — steps
>   appear only as prose examples — so there is no table owing the new `startsStream` step.
>
> The lesson is the one this plan is built on, turned on the plan itself: **a grep is not a read.**
> Counting the word "console" across four files produced a confident, wrong finding in minutes;
> opening the files disproved it in minutes too.

So: the docs are in better shape than claimed, and there is **no known-stale docs work**. A read is
still worth doing — nobody has run `getting-started.md` as written against 0.26.3 — but it is
speculative maintenance, not a known defect, and it should be ranked accordingly.

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

The manual pass here is **not** "add view tests" — it is *look at the thing running*.

> **CORRECTED 2026-09-19.** This originally cited #20/#21/#22 as live UI hazards, two of them silent
> data loss. **All three were already fixed** — #19/#20/#21 in `4a128b8` on 2026-09-04, #22 in the
> console fold — and simply never closed. They are closed now, with the tests that pin them.
> Stoat's open list went from 12 to 8 without a line of code being written, which is its own finding:
> **the issue list was the stalest artifact in either repo.**

That removes the concrete starting hypotheses this section had. A walk-through is still worth doing
on a UI whose views are untested, but it starts from nothing known-broken.

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
