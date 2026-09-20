# Handoff — Bobcat docs + public API review

**2026-09-20.** `main` at **7ab8d16**, latest tag **v0.26.3**, **no open PRs**, working tree clean
apart from untracked front-end `dist/` artifacts and `.claude/plans/2026-09-15-event-model-open-issues.md`
(not mine — left alone).

**Bobcat has one open issue: #109** (Rider plugin, not scheduled). Stoat has 8.

---

## Where Event Modeling actually is

**The generation path is done and proven, and the proof is a repository.** CritterCrush regenerates
from a curated model on 0.26.3 with **zero structural edits** — nothing the scaffolder emits has to
be replaced — and runs **88 specs green** in ~47s. Branch
`crittercrush/regenerated-on-0.26.3` in `JasperFx/CritterStackSamples`, pushed.

`#324`, the umbrella that asked for all of this, was closed on 2026-09-19 with all four parts
shipped across 0.24.0 → 0.26.3.

**What closed the last of the hand-editing**, in order of how much it was worth:

| | effect |
|---|---|
| `refusedWith:` / declaring all eleven 404s (#337) | `[WriteModel]` nullable 12 → 2; ten endpoints stopped needing a hand flip |
| `defaults.fixture:` (#356) | seven spec classes stopped needing the same two hand edits each |
| `defaults:` + `scaffold:` (#334) | the manifest went 107 lines → 32 |
| `[EmptyResponse]` (#346), no `[ReadModel]` on `Validate` (#345) | 13 empty records and 13 wrong concepts gone |
| marker interface + one `Evolve` (#347) | the two views 102 lines each → 63 and 54 |

**What still needs a hand after generation**, and neither is a defect:

1. **`[property: Identity]` on seven command records.** The stream identity is a field of the
   command and nothing in the curated format says which. The scaffold's own comment points at the
   fix, so it is documented rather than automated. **Unfiled** — decide whether the model could
   state it.
2. **Status vocabularies are new files.** The format knows six scalar types, so every status is
   `string` and `AppointmentStatus` / `HomeCheckStatus` / `VolunteerApplicationStatus` /
   `AppointmentKind` are written by hand. Jeremy: enums are the preference, but "that fine grained
   control may be beyond the scope of our generator."

---

## The next step: a manual review of the docs and the public API

Jeremy's stated preference. Both are **reads**, not test-writing — see "why" at the bottom.

### Docs — 15 files, 3,395 lines

**There is no known-stale work.** A previous version of the review plan claimed four docs still
described the console as part of Bobcat and that three releases were undocumented; **both were
wrong** and are corrected in `.claude/plans/2026-09-19-event-modeling-manual-review.md`. The split
updated the docs properly, and the curated-format reference lives in `ai-skills` **by design** — no
Bobcat doc documents the `then:` vocabulary or carries a grammar step table.

So this is a read for quality, not a hunt for known breakage. Suggested order by
risk-per-line-read:

```
  46  getting-started.md      <- run it as written against 0.26.3; worst first impression if wrong
 122  command-line.md         <- every command, actually executed
 514  marker-steps.md         <- the projected lane, which is what CritterCrush is
 519  sample-wiring.md        <- the longest, and the most likely to have drifted
 287  composing-grammars.md
 263  code-first-specs.md
 440  editor-integration.md
 276  parallel-ready-suites.md
 314  monitor-design.md       <- verify it describes Stoat's console correctly post-split
 136  ledger-design.md
 135  versions.md
 116  dotnet-test.md
 109  wolverine-ci-rollout.md
  84  spec-identities.md
  34  index.md
```

### Public API — 58 types across the two model packages, 4 of them actually consumed

`Bobcat.EventModel` **33 public types**, `Bobcat.EventModel.Scaffolding` **25**,
`Bobcat.CritterStack` **15**.

**The sharpest finding to start from: the only external consumer calls four of them.** CritterCrush's
`models/Scaffolder/Program.cs` — the single non-test consumer of the scaffolding API — uses
`CuratedModelReader`, `SliceScaffolder`, `SpecOwnershipPlan`, `SpecOwnershipReader`. Nothing else.

**Eight of the 25 scaffolding types are emission frames** — `AggregateFrame`,
`ChapterEventsHeaderFrame`, `CollapsedEndpointFrame`, `EndpointTranslationFrame`,
`MarkerInterfaceFrame`, `RecordFrame`, `ViewSliceFrame`, `WriteModelHandlerFrame` — public with no
consumer outside the assembly. `ScaffoldFrame`, `SlicePlan`, `HistoryArrangement*`, `BusVisibility`,
`TriggerOrigins`, `MarkerInterface`/`MarkerView` are the same shape of question.

Questions worth deciding rather than discovering later:

- Which of those 58 were ever **meant** to be public? The frames read as "public because the
  emitters are in the same folder", not as a designed surface.
- `Bobcat.EventModel` exposes the whole `Curated*` object graph (16 types). That is the format's
  in-memory shape — is it an API, or an implementation detail that a reader/writer pair should
  bracket?
- **Do not freeze anything before the docs pass.** The docs are what say which surface was ever
  intended to be public.

> **Do not chase `StoatPlan`.** The CritterCrush runner calls it and it is not in any Bobcat
> assembly — because `StoatPlan.cs` is a **local file in that runner**. Verified 2026-09-20; it
> looks exactly like a type that vanished in the split and is not one.

---

## Everything else, for a cold start

### What shipped 2026-09-18/19

**Bobcat 0.26.3**, 14 packages live. The release nearly shipped without its own tool: the console
split had renamed the project to `Bobcat.Cli` and it never opted into `IsPackable`, which
`src/Directory.Build.props` defaults to **false** — `dotnet pack` produced no nupkg, silently, zero
exit code. Renamed back to `Bobcat.Console` and opted in. **Pre-flight is: pack the solution and
COUNT.** 14 from one pack now; it used to be 13 + 1 packed separately.

Ten scaffolder defects fixed (#344, #345, #346, #347, #349, #351, #355, #356, #357, #360), **all
found by reading generated code, none by a test**. One of them (#351) was mine, shipped in 0.26.1: a
static `Evolve` where the base declares a virtual, which compiles and does not register. 128 green
text-assertion tests did not see it.

**ai-skills**: `Evolve` nullability (#210) and the format docs catching up (#211) — `refusedWith:`
had been undocumented since 0.26.0 and the spec-ownership manifest had **no documentation at all**.

### Five issues closed with no code written

stoat **#19/#20/#21/#22** and bobcat **#324** were already fixed and never closed. Stoat went 12 → 8,
Bobcat to 1. **The issue list was the stalest artifact in either repo** — staler than any doc, and
the thing most likely to send someone hunting a bug that no longer exists. Check before believing an
open issue.

### Traps this session actually hit

- **A grep is not a read.** Counting the word "console" across four docs produced a confident wrong
  finding in minutes. Opening them disproved it in minutes.
- **A CI watch must name the check it waits on.** "Are all visible checks done" is TRUE before the
  slow ones register — it reported green while `test` was still running. Also: a push to `main` runs
  **3** checks, a PR runs **4** (`build` does not run on `main`), so direct-to-main is less gated.
- **`for p in $PKGS` does not word-split in zsh** — reported 0/14 packages live for a version that
  was fully published. Use an array.
- **`git add -A`** swept 13 front-end `dist/` artifacts into a rename commit. Stage explicitly.
- Text-asserting tests cannot see a projection that will not register. `ScaffoldCompilesTests`
  asserts text despite its name; `Bobcat.Marten.Tests.ProjectionRegistrationTests` boots a host, and
  is where that class of check belongs.

### Working copies

| path | what it is |
|---|---|
| `~/code/crittercrush-clean` | **the current one.** Branch `crittercrush/regenerated-on-0.26.3`, pushed, 88 green. Has `.scaffold-output/` — the pristine 0.26.3 scaffold beside the implementation, so a reader can tell generated from hand-written — and `./review.sh <path>` to diff one file |
| `~/code/crittercrush-regen` | the 0.26.1 pass that FOUND the defects. Reference only, unpushed |
| `~/code/crittercrush-lane-a` | the original pass. Reference only; its `LANE-A-REVIEW.md` has been stale since before this week |

### Running CritterCrush

Postgres on **5433** — yesterday that was this repo's own compose file, not Wolverine's shared
container. `docker compose up -d` in `~/code/crittercrush-clean` if 5433 is dead.

```bash
cd ~/code/crittercrush-clean/CritterCrush
dotnet build CritterCrush.sln -c Release
./CritterCrush.Specs/bin/Release/net10.0/CritterCrush.Specs
```

Run the exe, not `dotnet test` — the latter hides per-test output.

### Why the next step is reading and not tests

Ten defects this week, all found by reading generated output, none by a test. Every one compiled and
most of them ran. Tests written blind against the current behaviour would pin whatever is wrong
about it — which is also why the review plan argues against writing Stoat view tests before anyone
has looked at the views.
