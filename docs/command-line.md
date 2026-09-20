# The Command Line

::: tip
We *think* this option will mostly appeal to folks using AI assisted development
:::

You have two options for making your Bobcat Gherkin test project executable:

1. Use the `Bobcat.Mtp` Nuget to enable Bobcat specifications to run from `dotnet test` and your IDE
2. Use the command line runner shown in this page 

## Which one do you want?

| | `dotnet test` and your IDE | The Bobcat command line |
|---|---|---|
| You get | one scenario = one test node, the green arrow in the gutter, the debugger, `dotnet test`, CI | `run`, `list`, **`preview`**, **`interactive`** |
| The project needs | `Bobcat.Mtp`, and **no `Main` of your own** | a `Main` that calls `BobcatRunner.Run` |
| Set up in | [Running Specs with `dotnet test`](dotnet-test.md) · [Integrating with Your IDE](tutorials/ide-integration.md) | this page |

**Most projects should take the first column.** It is less setup, it runs in CI exactly the same
way, and it is the only one that gives you a test explorer and a debugger. If you are not sure,
that is your answer — start at
[Integrating Bobcat with Your IDE](tutorials/ide-integration.md).

Take the command line when you want the two things `dotnet test` has no equivalent for:

- **`preview`** — every step with the fixture method it bound to, executing nothing. The tool for
  "why did my step match the wrong grammar."
- **`interactive`** — pick scenarios from a live prompt with the suite's resources held warm, so a
  run-tweak-run-again loop stops paying database and host startup each time.

A pure Gherkin spec project driven from a terminal and a pipeline is the case this fits. A project
that also holds ordinary unit tests, or whose authors live in an IDE, is better served by the first
column.

::: warning You cannot have both in one project
The command family needs a `Main` of your own. The moment you declare one, the generator stops
emitting the MTP entry point — and `dotnet test` then feeds its protocol flags to Bobcat's parser:

```
error run failed: Unknown argument or flag for value --internal-msbuild-node
```

The two argument surfaces never mix, by design. If you want both, put the specs in two projects or
take the `dotnet test` column and drive `preview` from a scratch host.
:::

## Setup

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Bobcat" />
  <PackageReference Include="Bobcat.Generators" />
  <AdditionalFiles Include="Features/**/*.feature" />
</ItemGroup>
```

```csharp
using System.Reflection;
using Bobcat.Runtime;                       // BobcatRunner lives here, not in `Bobcat`

public static class SpecsRunner
{
    public static Task<int> Main(string[] args) => BobcatRunner.Run(args, runner =>
    {
        runner.ScanForFeatures(Assembly.GetExecutingAssembly());
        // runner.Suite.AddResource(new AlbaResource<Program>());
    });
}
```

**`ScanForFeatures` is not optional.** It is what hands the generator's output to the runner;
without it every command below runs and finds nothing. Use an explicit `static class` rather than
top-level statements — see [footgun 1](sample-wiring.md#_1-program-symbol-collision-when-specsrunner-uses-top-level-statements).

## The commands

`run` is the default, so a bare `dotnet run` executes the suite. Every form below works from the
project directory, or from anywhere with `--project <path>`.

| Command | What it does | Starts resources? |
|---|---|---|
| `run` (the default) | Execute the discovered scenarios and render the results | yes |
| `list` | Feature and scenario titles, nothing executed | no |
| `preview` | Every step **with its binding**, executing nothing | no |
| `interactive` | Pick scenarios from a live prompt, resources kept warm | on the first run only |

All four take `-f, --feature <text>` (case-insensitive substring of the feature title) and
`-t, --tag <tag>`. `run` alone adds `-j, --json`.

`dotnet run -- help` lists them; `dotnet run -- help <command>` shows one command's usage.

### `run`

```bash
dotnet run
```

```
Feature: Calculator
════════════════════

  Add two numbers OK
  ─────────────────────────
    ✓ Given the left operand is 25
    ✓ Given the right operand is 17
    ✓ When  the operands are added
    ✓ Then  the result is 42

  Succeeded with Rights: 4, Wrongs: 0, Errors: 0
  Duration: 1ms

═══════════════════════════════════════════
  Succeeded with Rights: 8, Wrongs: 0, Errors: 0
  2/2 scenarios passed

  Timing — 1ms measured across 2 scenario(s)
    • Add two numbers 1ms (100% of measured time) — steps 0ms, lifecycle 0ms
    step the left operand is {number} cost 0ms across 2 occurrence(s)
    lifecycle ResetAll cost 0ms across 2 scenario(s)
```

### `list`

```bash
dotnet run -- list
dotnet run -- list --tag slow
```

```
Feature: Calculator
  Fixture: CalculatorFixture
  - Add two numbers @arithmetic
  - Subtract two numbers @arithmetic @slow
```

The fixture is named because a feature binding to the wrong one — or to none — is the most common
wiring mistake, and this is where it shows.

### `preview` — see the bindings, not just the prose

```bash
dotnet run -- preview
dotnet run -- preview --feature calculator
```

```
Feature: Calculator
════════════════════

  Subtract two numbers @arithmetic @slow
  ──────────────────────────────
    ○ Given the left operand is 25
      ↳ CalculatorFixture.TheLeftOperandIs — "the left operand is {int}"
        value ← "25" (capture)
    ○ When  the operands are subtracted
      ↳ CalculatorFixture.Subtracted — "the operands are subtracted"
    ○ Then  the result is 8
      ↳ CalculatorFixture.TheResultIs — "the result is {int}"
        expected ← "8" (capture)
```

`↳` is the method the step bound to and the expression it matched; the line under it is where each
parameter's value came from — a Cucumber capture, a table column, an injected service, or a
decision table's expected-output cell.

**Preview never starts a resource.** The plan is pure in-memory composition, built before
`StartAll` would run, so a suite whose database is down previews fine. Steps with no binding
metadata (code-first specs, hand-built definitions) render with a quiet note instead.

### `interactive` — a REPL for specs

```bash
dotnet run -- interactive
```

A Spectre selection prompt over the feature/scenario tree; each selection can be **run** or
**previewed**, and picking a feature row runs all of its scenarios.

The point of the mode is the warm suite: resources start **once, lazily on the first run** (a
preview-only session starts nothing) and stay up between selections, so the loop stops paying
Postgres or host startup each time. Every scenario still gets the full `ResetAll` →
scenario-scope → teardown bracket per run; warmth never means dirty state. Everything is disposed
once, on exit.

It requires a real terminal. Under redirected input or a non-interactive console it refuses rather
than hanging:

```
The interactive command needs a terminal, but standard input is redirected — an interactive
prompt here would hang forever. Use 'list' to see the scenarios, 'preview' to inspect them,
or 'run --feature <name>' to run a subset.
```

### `--json` — the machine-readable report

```bash
dotnet run -- run --json
```

Replaces the console rendering. The report carries the exit code and counts, then every feature and
scenario with **per-step status and start offset**, per-scenario lifecycle phases, aggregate step
timings, and a `gaps` array for time inside a scenario that no step or lifecycle phase accounts for:

```json
{
  "exitCode": 0,
  "counts": { "rights": 8, "wrongs": 0, "errors": 0, "succeeded": true },
  "features": [
    {
      "title": "Calculator",
      "scenarios": [
        {
          "title": "Add two numbers",
          "succeeded": true,
          "durationMs": 1,
          "lifecycle": [ { "name": "ResetAll", "startedAtMs": 0, "durationMs": 0 } ],
          "steps": [
            { "stepId": "TheLeftOperandIs", "kind": "Given",
              "text": "the left operand is 25", "status": "success", "startedAtMs": 1 }
          ]
        }
      ]
    }
  ],
  "timing": {
    "steps": [ { "text": "the left operand is {number}", "occurrences": 2, "totalMs": 0, "maxMs": 0 } ],
    "gaps":  [ { "scenario": "Add two numbers", "after": "BeginScenarioAll",
                 "before": "EndScenarioAll", "durationMs": 1 } ]
  }
}
```

This is the report to feed a build summary, a dashboard, or an agent.

## Filters

```bash
dotnet run -- run --feature "Checkout"     # substring of the feature title, case-insensitive
dotnet run -- run --tag smoke              # exact tag, case-insensitive
```

Both narrow every command, and they combine.

Write the tag **without** the `@`. `--tag smoke` matches `@smoke`; `--tag @smoke` matches nothing.

`run` treats a filter that matched nothing as a failure (exit 2) rather than a pass — see below.
`list` and `preview` do not, since inspecting an empty selection is a reasonable thing to ask for.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Every scenario passed. A pass-on-retry still exits 0, reported separately |
| `1` | A regression failure — **or** a usage error, including an unrecognized flag |
| `2` | Nothing was discovered or the filters matched nothing; a resource failed to start; preflight failed; `SpecCatastrophicException` |

Two of these are worth designing a pipeline around.

**`2` includes "no specs were discovered,"** and that is the entry that protects you from the worst
CI outcome — a green pipeline over a suite that ran nothing:

```
No specs were discovered, so this run asserted nothing. A .feature file must be an
<AdditionalFiles> item in the project the generator runs in, and its fixture must bind
(BOBCAT001). Set BobcatRunner.RequireSpecs = false if an empty run is expected here.
```

Set `BobcatRunner.RequireSpecs = false` only where an empty run is genuinely expected.

**`1` covers both a real failure and a typo in your own pipeline flags**, because an unknown flag is
an error rather than being silently ignored:

```
$ dotnet run -- run --bogus
Invalid usage
Unknown argument or flag for value --bogus
```

That is the right behaviour, but it means a malformed CI invocation looks like a failing suite until
someone reads the output.

## See also

- [Running Specs with `dotnet test`](dotnet-test.md) — the other surface, and the one most projects want
- [Integrating Bobcat with Your IDE](tutorials/ide-integration.md) — scenarios in the test explorer
- [Integrating Bobcat with CI](tutorials/continuous-integration.md) — putting either surface in a pipeline
- [The `bobcat` Tool](bobcat-tool.md) — a separate global tool for Event Model files, unrelated to running specs
