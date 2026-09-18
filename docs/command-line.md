# The Command Line

Every spec host built on `BobcatRunner.Run(args, configure)` carries a JasperFx command family
(issue #206), so a runner and its help text come from the same machinery as every other JasperFx
tool:

```bash
dotnet run --project src/MySpecs -- run          # or ./MySpecs run for a published host
dotnet run --project src/MySpecs -- help         # list the commands
dotnet run --project src/MySpecs -- help run     # usage for one command
```

> [!NOTE]
> This is the **plain runner's** surface. A host exposed through Microsoft.Testing.Platform
> (`BobcatTestApplication.Run`) is a separate entry with its own flags — `--list-tests`,
> `--filter-uid`, `--filter-feature`, `--filter-tag` — covered in
> [Running Specs with `dotnet test`](dotnet-test.md). The two argument surfaces never mix.

## Commands

| Command | What it does |
|---|---|
| `run` (the default) | Execute the discovered features and render the results |
| `list` | List features and scenarios without running them |
| `preview` | Render scenarios **with their step bindings**, executing nothing |
| `interactive` | Pick scenarios to run or preview from a live prompt, resources kept warm |

All four accept the same filters — `-f, --feature <text>` (case-insensitive substring of the
feature title) and `-t, --tag <tag>` — and `run` alone adds `-j, --json` for the machine-readable
report instead of the console rendering.

## `preview` — see the bindings, not just the prose

Preview shows the one thing the `.feature` file cannot: which fixture method each step matched,
and where every parameter's value comes from — a Cucumber capture, a table column, an injected
service, or a decision table's expected-output cell.

```
Feature: Calculator
════════════════════

  Add two numbers
  ─────────────────────────
    ○ Given the left operand is 25
      ↳ CalculatorFixture.TheLeftOperandIs — "the left operand is {int}"
        value ← "25" (capture)
```

That makes it the tool for "why did my step match the wrong grammar" — and a scaffolded feature
can be sanity-checked before anything runs. Preview **never starts a resource**: the plan is
built before `StartAll`, so a suite whose database is down previews fine. Steps without binding
metadata (code-first specs, hand-built definitions) render with a quiet note instead.

## `interactive` — a REPL for specs

`interactive` puts a Spectre selection prompt over the feature/scenario tree; each selection can
be **run** or **previewed**, and picking a feature row runs all of its scenarios.

The point of the mode is the warm suite: resources are started **once, lazily on the first run**
(a preview-only session starts nothing), and stay up between selections — so the
run-tweak-run-again loop stops paying Postgres or host startup each time. Every scenario still
gets the full `ResetAll` → scenario-scope → teardown bracket per run; warmth never means dirty
state. Everything is disposed once, on exit.

The prompt requires a real terminal. Under redirected input or a non-interactive console the
command **refuses with a clear message and exit code 1** rather than falling back silently — a
misconfigured CI job should fail loudly, not hang or quietly do something else.

## The `bobcat` tool

Separate from everything above. `Bobcat.Console` is a global tool — `dotnet tool install -g
Bobcat.Console` — and it is a **plain command host**: no web server, no store, nothing that outlives
the process. It carries the free, no-server half of the toolset, which today is reading,
validating and converting [Event Model](https://eventmodeling.org) files.

> [!NOTE]
> The `bobcat` tool used to host the live run console. It does not any more — the board, the
> archive, the model store and the design-time canvas all moved to
> [Stoat](https://github.com/JasperFx/stoat) on 2026-09-18, because every one of them remembers
> something across a process and this repository holds nothing that gates on a licence. Bobcat is
> the format and the run. Its runs still publish to that console exactly as before; see
> [What a Run Publishes](monitor-design.md).

### `import-event-model`

```bash
bobcat import-event-model Wallet.emodel.yaml
```

It sniffs the file and takes either shape:

- **The curated format** (`schema` / `model` / `slices`) is read and validated. Warnings print
  whether or not it validated — a file carrying nothing but warnings validates, which is exactly
  the silence the warning exists to break. Validation problems go to stderr and the command fails.
- **An [eventmodelers.ai](https://eventmodelers.ai) emlang board export** is segmented into slices
  and **written out as a curated file to review** — `<Model>.emodel.yaml` beside the input by
  default. The segmentation is a set of reported guesses, and a wrong guess should be a one-line
  diff in that file rather than a re-import.

Either way it prints the model name, the slice count, and how many specifications are bound.

| Flag | What it does |
|---|---|
| `-m, --model <name>` | Model name for an emlang import; defaults to the file name. The curated format carries its own |
| `--namespace <ns>` | Root namespace recorded for synthesized type names on an emlang import |
| `-o, --out <path>` | Where an emlang import writes the reviewable curated file |
| `-u, --url <base>` | Push the assembled model to a run console at this base URL, e.g. `http://localhost:5525` |

`--url` takes the console's **base** URL and appends `/api/event-model` itself; pointing it at the
endpoint directly is the documented trap (the base answers 404, the endpoint answers 204).

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Every scenario passed (a pass-on-retry still exits 0, reported separately) |
| `1` | Regression failure — or a usage error, including an unrecognized flag |
| `2` | Catastrophic: a resource failed to start, preflight failed, or `SpecCatastrophicException` |

Two behaviours changed when the hand-rolled parser retired: an **unknown flag is now an
error** (it used to be silently ignored), and **`list` respects `--feature`/`--tag`** instead of
always printing everything.
