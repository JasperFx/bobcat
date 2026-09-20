# Integrating Bobcat Gherkin

You have two options for making your Bobcat Gherkin test project executable:

1. **[`dotnet test` and your IDE](#dotnet-test)** — the `Bobcat.Mtp` package exposes each scenario
   as an ordinary test node.
2. **[The command line runner](#command-line-runner)** — your own `Main` over `BobcatRunner`, which
   adds `preview` and `interactive`.

## Which one do you want?

| | [`dotnet test` and your IDE](#dotnet-test) | [The command line runner](#command-line-runner) |
|---|---|---|
| You get | one scenario = one test node, the green arrow in the gutter, the debugger, `dotnet test`, CI | `run`, `list`, **`preview`**, **`interactive`** |
| The project needs | `Bobcat.Mtp`, and **no `Main` of your own** | a `Main` that calls `BobcatRunner.Run` |

**Most projects should take the first.** It is less setup, it runs in CI exactly the same way, and
it is the only one that gives you a test explorer and a debugger. If you are not sure, that is your
answer — and [Integrating Bobcat with Your IDE](tutorials/ide-integration.md) walks it end to end.

::: tip
We *think* the command line runner will mostly appeal to folks using AI assisted development.
:::

Take the command line runner when you want the two things `dotnet test` has no equivalent for:

- **`preview`** — every step with the fixture method it bound to, executing nothing. The tool for
  "why did my step match the wrong grammar."
- **`interactive`** — pick scenarios from a live prompt with the suite's resources held warm, so a
  run-tweak-run-again loop stops paying database and host startup each time.

::: warning You cannot have both in one project
The command line runner needs a `Main` of your own. The moment you declare one, the generator stops
emitting the MTP entry point — and `dotnet test` then feeds its protocol flags to Bobcat's parser:

```
error run failed: Unknown argument or flag for value --internal-msbuild-node
```

The two argument surfaces never mix, by design. If you want both, put the specs in two projects.
:::

---

## `dotnet test` and your IDE {#dotnet-test}

`Bobcat.Mtp` exposes a spec project as a Microsoft.Testing.Platform test host: **one scenario is one
test node**, so `dotnet test`, IDE Test Explorers, CI, and the Bobcat supervisor all see scenarios
as ordinary tests.

### Setup

A spec project needs no hand-written `Main`. Reference the packages, add the `.feature` files, make
the project an executable test host:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Bobcat" />
  <PackageReference Include="Bobcat.Mtp" />
  <PackageReference Include="Bobcat.Generators" />
  <AdditionalFiles Include="Features/**/*.feature" />
</ItemGroup>
```

::: warning Both MSBuild properties are load-bearing
Without them `dotnet test` does not recognize the project as a test project. It restores, prints
nothing about tests, and **exits 0** — a passing pipeline over a suite that never ran. Put them in
`Directory.Build.props` rather than a per-job `-p:` override, so no build can produce a host without
them.
:::

`Bobcat.Generators` detects that the compilation references `Bobcat.Mtp` and declares no entry point
of its own, and emits one (`BobcatEntryPoint.g.cs`): a `Main` that goes through
`BobcatTestApplication.Run`, scans the assembly for generated features and code-first
specifications, and calls every `[BobcatConfiguration]` method.

Check it worked:

```bash
$ dotnet test
  Run tests: '.../MySpecs.dll' [net10.0|arm64]
  Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 38ms
```

```bash
$ ./MySpecs --list-tests
  Calculator: Add two numbers
  Calculator: Subtract two numbers
```

### Configuring the suite

Most real suites register resources. Mark any static method with `[BobcatConfiguration]` and the
generated `Main` calls it — this is where a hand-written `Main`'s configure lambda would have gone:

```csharp
public static class SuiteConfiguration
{
    [BobcatConfiguration]
    public static void Configure(BobcatRunner runner)
    {
        runner.Suite.AddResource(new AlbaResource<Program>());
        runner.RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 };
    }
}
```

The method must be `static void` with exactly one `BobcatRunner` parameter, reachable from generated
code (a wrong shape is a compile error, `BOBCAT016`). Several such methods are called in a
deterministic order — sorted by declaring type, then method name.

### When the entry point is NOT generated

The generator abstains, in order, when:

1. the compilation does not reference `Bobcat.Mtp`;
2. the MSBuild property `BobcatGenerateEntryPoint` is `false` (the opt-out);
3. the project is not an executable (`OutputType` must be `Exe`);
4. **the assembly declares its own entry point.** A hand-written `Main` always wins — every
   pre-#207 consumer keeps compiling unchanged, and `CS0017` is impossible. A
   `[BobcatConfiguration]` method left behind in that situation is reported (warning `BOBCAT017`)
   rather than silently ignored, because the hand-written `Main` will not call it.

Hand-writing `Main` remains fully supported and is the escape hatch for anything the seam does not
cover. Note this is `BobcatTestApplication.Run`, which keeps you on the MTP surface — not
`BobcatRunner.Run`, which moves you to [the command line runner](#command-line-runner):

```csharp
public static class SpecsRunner
{
    public static Task<int> Main(string[] args)
        => BobcatTestApplication.Run(args, runner =>
        {
            runner.ScanForFeatures(typeof(SpecsRunner).Assembly);
            runner.Suite.AddResource(new AlbaResource<Program>());
        });
}
```

In-repo `ProjectReference` consumers do not inherit `Bobcat.Mtp`'s `buildTransitive` props, so they
must set `<GenerateTestingPlatformEntryPoint>false</GenerateTestingPlatformEntryPoint>` themselves
(that is the platform's synthesized entry point, a different thing from Bobcat's).

### Filtering

The host is runnable directly (`./MySpecs`) and through `dotnet test`; arguments after `--` go to
the test host:

```bash
# The platform's own uid filter — one scenario, exactly
dotnet test -- --filter-uid "Ordering/An order is accepted"

# By feature title (case-insensitive substring) …
dotnet test -- --filter-feature "Ordering"

# … and by Gherkin tag (case-insensitive, exact)
dotnet test -- --filter-tag regression

# They intersect, and both narrow --list-tests too
./MySpecs --list-tests --filter-feature "Shipping"
```

**Write the tag without the `@`** — the platform consumes `@`-prefixed arguments as response files.

These are the same levers the [command line runner's](#filters) `--feature` / `--tag` offer, with
the same semantics, and they work against hand-written-`Main` hosts too — the options register
inside `BobcatTestApplication.Run`.

### Resources under IDE runs — a cost to know about

Discovery never starts resources (IDEs discover on every build). **Execution always pays the suite's
full resource start-up, however few scenarios were selected**: running one scenario from Test
Explorer still runs `StartAll` for every registered resource — the database, the Docker containers,
the application host. That is inherent to the model — a scenario's meaning includes the resources it
runs against, and Bobcat will not guess which ones a subset needs — so budget a single-scenario IDE
run at roughly resource start-up plus the scenario, not the scenario alone. Keeping `Start()` fast
(reuse running containers, `docker compose up -d` out of band) is the lever that matters.

When you are iterating rather than running once, [`interactive`](#interactive) keeps resources warm
between runs instead.

---

## The command line runner {#command-line-runner}

### Setup

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

### The commands

`run` is the default, so a bare `dotnet run` executes the suite. Every form below works from the
project directory, or from anywhere with `--project <path>`.

| Command | What it does | Starts resources? |
|---|---|---|
| `run` (the default) | Execute the discovered scenarios and render the results | yes |
| `list` | Feature and scenario titles, nothing executed | no |
| `preview` | Every step **with its binding**, executing nothing | no |
| `interactive` | Pick scenarios from a live prompt, resources kept warm | on the first run only |

All four take `-f, --feature <text>` and `-t, --tag <tag>`. `run` alone adds `-j, --json`.

`dotnet run -- help` lists them; `dotnet run -- help <command>` shows one command's usage.

#### `run`

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

#### `list`

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

#### `preview` — see the bindings, not just the prose

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

**Preview never starts a resource.** The plan is pure in-memory composition, built before `StartAll`
would run, so a suite whose database is down previews fine. Steps with no binding metadata
(code-first specs, hand-built definitions) render with a quiet note instead.

#### `interactive` — a REPL for specs {#interactive}

```bash
dotnet run -- interactive
```

A Spectre selection prompt over the feature/scenario tree; each selection can be **run** or
**previewed**, and picking a feature row runs all of its scenarios.

The point of the mode is the warm suite: resources start **once, lazily on the first run** (a
preview-only session starts nothing) and stay up between selections, so the loop stops paying
Postgres or host startup each time. Every scenario still gets the full `ResetAll` → scenario-scope →
teardown bracket per run; warmth never means dirty state. Everything is disposed once, on exit.

It requires a real terminal. Under redirected input or a non-interactive console it refuses rather
than hanging:

```
The interactive command needs a terminal, but standard input is redirected — an interactive
prompt here would hang forever. Use 'list' to see the scenarios, 'preview' to inspect them,
or 'run --feature <name>' to run a subset.
```

#### `--json` — the machine-readable report

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

### Filters {#filters}

```bash
dotnet run -- run --feature "Checkout"     # substring of the feature title, case-insensitive
dotnet run -- run --tag smoke              # exact tag, case-insensitive
```

Both narrow every command, and they combine.

Write the tag **without** the `@`. `--tag smoke` matches `@smoke`; `--tag @smoke` matches nothing.

`run` treats a filter that matched nothing as a failure (exit 2) rather than a pass — see below.
`list` and `preview` do not, since inspecting an empty selection is a reasonable thing to ask for.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Every scenario passed. A pass-on-retry still exits 0, reported separately |
| `1` | A regression failure — **or** a usage error, including an unrecognized flag |
| `2` | Nothing was discovered or the filters matched nothing; a resource failed to start; preflight failed; `SpecCatastrophicException` |

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

---

## See also

- [Integrating Bobcat with Your IDE](tutorials/ide-integration.md) — scenarios in the test explorer, end to end
- [Integrating Bobcat with CI](tutorials/continuous-integration.md) — putting either surface in a pipeline
- [Parallel-Ready Suites](parallel-ready-suites.md) — what the supervisor needs on top of an MTP host
- [The `bobcat` Tool](bobcat-tool.md) — a separate global tool for Event Model files, unrelated to running specs
