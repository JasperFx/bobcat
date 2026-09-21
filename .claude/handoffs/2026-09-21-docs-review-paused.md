# Handoff — documentation review, paused for structural work

**2026-09-21.** `main` at **331d8ec**, pushed, CI green. **bobcat.jasperfx.net is current with
main** — deployed through the new `docs.yml`. Two open issues: **#374** and **#109**.

Working tree carries `src/Bobcat/notes.md` **modified by Jeremy, deliberately left unstaged**, and
three untracked paths that are not mine (`.claude/plans/2026-09-15-…`,
`src/Bobcat.EventModel.FrontEnd/`, `src/Bobcat.Monitor.FrontEnd/`).

---

## STOP HERE FIRST: the structural change about to land

`src/Bobcat/notes.md` is now Jeremy's review notes, and it says:

> ## ITestResource
> * have this implement `IHostedService`
> * `Start` --> `StartAsync`

**That change invalidates documentation written yesterday.** Three files describe the current shape
and must move with it, together, or the docs will describe an API that no longer exists:

| file | what breaks |
|---|---|
| `docs/resources.md` | The **four verbs** table (`Start` / `ResetBetweenScenarios` / `Recycle` / `Restart`) is the spine of the page. The "Writing your own" sample implements `Start()`. Both are wrong the moment `Start` becomes `StartAsync` |
| `docs/run-lifecycle.md` | The lifecycle diagram's first line is `StartAll`, and the "Starting up" section explains a resource being recorded as *attempted* before `Start` is called |
| `docs/integrations/*.md` | Each shows `AddResource(new XResource(...))`; if registration changes shape with `IHostedService`, all three follow |

Nothing else in `docs/` names `ITestResource`'s verbs. Grep `Start()` and `ITestResource` before
assuming.

**Do not start the remaining page reviews until this settles.** Two of the unread pages
(`parallel-ready-suites.md`, `monitor-design.md`) touch resources, and reviewing prose against an
API mid-change is wasted work.

---

## What shipped, 2026-09-20 → 21

Nine commits. The docs went from "organized around Bobcat's parts" to "organized around what a
reader is trying to do", which was Jeremy's framing.

### Structure

- **Tutorials** (`docs/tutorials/`) — 8 pages. Five written; **three are outlines** with the gap
  stated at the top: Data Intensive Specifications, Event Modeling and SDD, Agent Friendly Tests.
- **Integrations** (`docs/integrations/`) — Alba, Marten, Wolverine, plus an index.
- **Runtime model** — `docs/run-lifecycle.md` and `docs/resources.md`, both new. Before them,
  `IRecyclableResource`, `IGlobalAction` and `Preflight` had **zero** doc coverage.
- **Merged** — `command-line.md` + `dotnet-test.md` → `docs/integrating-gherkin.md`, opening with
  which surface to pick. The `bobcat` tool split to `docs/bobcat-tool.md`.
- **Dissolved** — `sample-wiring.md` split (playbook internal, footguns public), then
  `wiring-a-real-host.md` distributed entirely: 5 already-absorbed footguns deleted, 6 to the pages
  that own them, 5 to `design/critter-stack-interop-notes.md`, 2 to the playbook.
- **Guides** — `docs/xunit.md`, `docs/tunit.md`.

### Internal content out of `docs/`

`design/` now holds `versions.md`, `ledger-design.md`, `wolverine-ci-rollout.md`, `rider/`,
`sample-wiring-playbook.md`, `critter-stack-interop-notes.md`, `code-first-specs-design.md`, and a
`README.md` that states the boundary and indexes them.

**Two mixed pages remain**: `docs/editor-integration.md` (VS Code setup is public, the Rider plugin
investigation is not) and `docs/monitor-design.md` (wire contract public, decisions of record not).
The `wiring-a-real-host` dissolve is the worked precedent.

### Code fixed

Eight CLI defects, six new regression tests, all green:

- `--tag @slow` matched nothing (#370) — the `@` is stripped by the parser but present in the file
  a user copies from.
- `list`/`preview` reported success in silence over an undiscoverable suite (#369) — they now
  describe it via `BobcatRunner.DescribeEmptySelection` and still exit 0. **That exit code is a
  deliberate decision**: they inspect, `run` asserts.
- Bare `bobcat` **hung forever** — `RunJasperFxCommands(args)` scans, so the tool inherited
  JasperFx's default `run` command and started a host. Now registers explicitly through
  `CommandExecutor.For`, the way `BobcatRunner.Run` always has. Killed the inherited 10 commands
  and the assembly-scan chatter with it.
- `import-event-model` on a non-model file exited 1 with **no output**; `--out` into a missing
  directory threw a raw `Interop.ThrowExceptionForIoErrno` stack after printing a successful-looking
  report.
- The unmatched-column diagnostic said "the column [X] **match** nothing" — only the noun was
  pluralized. An existing test had **pinned the bug** by asserting the fragment `"match nothing"`.

### Issues

Eight filed, six closed. Open: **#374** (should docs draw on CritterStackSamples/Marten/Wolverine
instead of this repo's `samples/`? — a decision only Jeremy can make, with a real sub-question about
whether citing another repo needs a compiled-snippet mechanism) and **#109**.

---

## Where the review stopped

| page | outcome |
|---|---|
| `getting-started.md` | 5 defects (#366) → rewritten, **verified by running it** |
| `command-line` + `dotnet-test` | 4 issues → merged |
| the command line runner (code) | root causes on #369 |
| `marker-steps.md` | 1 defect (#373) → fixed. **Structural note not acted on**: lines 264-514 are Event Modeling material for model-first repos and would land in the event-modeling tutorial, still an outline |
| `sample-wiring.md` | 3 defects (#371) → fixed, then dissolved |
| `composing-grammars.md` | **clean** — produced a code fix instead |
| `code-first-specs.md` | rewritten user-facing, design record moved |

**Unread**: `editor-integration.md` (440), `parallel-ready-suites.md` (276),
`monitor-design.md` (314), `spec-identities.md` (84).

---

## Method that worked, and should continue

**Run the samples, do not read them.** Every page reviewed by executing its instructions found
something reading had not. It caught five failures in `getting-started`, a `dotnet test` setup that
silently ran zero tests, a CLI that hung forever, and a grammar bug in an error message. It also
caught two defects in **my own drafts** before they shipped — a fixture using `List<string>` with no
`ImplicitUsings`, and `StepKind` living in `Bobcat.Engine`.

The pattern: a scratch project under the session scratchpad with a `ProjectReference` to the real
`.csproj`, paste the doc's sample verbatim, build, run.

**I was wrong twice, both times the same way** — filing a finding from a surface reading without
checking the thing underneath:

1. `design/versions.md` "states something untrue" — only the *heading* was wrong; the body already
   explained the deliberate split correctly.
2. `--url` "reports success on a doubled path" — `pushAsync` already checks
   `IsSuccessStatusCode`. My test listener answered 204 on any path. A probe that cannot fail
   proves nothing.

Both corrections are posted on the issues so nobody acts on the bad half.

---

## Traps hit

- **`git add src/` is not explicit enough.** It swept the 13 front-end `dist/` artifacts — the same
  sweep recorded in the previous handoff. Stage file paths.
- **A CI watch keyed on `.conclusion // .status` lies.** For an in-flight run both are empty, so a
  loop testing for `"in_progress"` falls through and reports done. Count completed-vs-total runs
  for the SHA instead.
- **Doc paths inside exception strings** are a coupling nothing tests. `BobcatRunner.cs` and
  `AlbaResource.cs` both carry one, and they had to be repointed **twice in one day**. A test
  asserting the referenced files exist would stop the third time being silent. **Unfiled.**
- A push arriving mid-session blocked a rebase because of Jeremy's unstaged `notes.md`; merged
  rather than stash his work. Hence the merge commit at 331d8ec.

## Docs deployment

`.github/workflows/docs.yml` (Jeremy's, 885b2b9) deploys to Cloudflare Pages, **`workflow_dispatch`
only**: `gh workflow run docs.yml --ref main`. Its build step is the docs' test suite — VitePress
fails on a dead internal link, which is what made the merges and the dissolve safe at speed.

It freezes the moment someone forgets to run it; a push trigger on `docs/**` is worth considering,
and was deliberately not added.
