# Integrating Bobcat with CI

**What you will build:** a pipeline step that runs your specifications, fails the build for the right
reasons, and hands back a failure a human can act on without re-running anything locally.

## 1. Run the suite the way you already do

A Bobcat spec project is an ordinary test project on xUnit v3, TUnit, NUnit or MSTest, so it runs in
CI exactly as it does locally:

```bash
dotnet test                        # familiar, and what most pipelines already call
./MySpecs                          # the executable -- per-test output that dotnet test hides
```

Prefer the executable in CI when you care about the output. `dotnet test` absorbs per-test stdout,
which is exactly the material you want when something fails on a machine you cannot attach to.

The exit code is your test platform's. On Microsoft.Testing.Platform a run that executed no tests at
all fails rather than passing, which protects you from the worst CI outcome -- a green pipeline over a
suite that ran nothing.

## 2. Make sure the specifications are really specifications

A spec project can pass while producing no specification at all, and nothing about the run can tell
you, because a suite that records nothing looks exactly like a suite with nothing to record. Two
things guard against it, both at build time:

- **`Bobcat.Generators` must be referenced.** Without it a step helper is an ordinary method call and a
  `SpecAssert` inside one reports to nothing.
- **BOBCAT028** warns about a `[BobcatFeature]` class that opens no recording. Treat it as an error in
  CI: `<WarningsAsErrors>$(WarningsAsErrors);BOBCAT028</WarningsAsErrors>`.

## 3. Filter deliberately

Filter with your framework's own options -- `--filter-class`, `--filter-method` and
`--filter-trait` on xUnit v3's Microsoft.Testing.Platform runner, or `dotnet test --filter`. A
Bobcat identity, `{Feature}/{Scenario}`, is translated to the framework's filter for you by the
[resident runner](../resident-runner.md), and `BOBCAT_LIST_SPECS` writes what the suite specifies;
see [Spec Identities](../spec-identities.md#listing-and-running-by-identity).

## 4. Get a readable failure

A failed specification fails its test with every gathered disagreement in the message, so the CI log
says which step was wrong and what the system actually did:

```
failed CalculatorSpecs.asserting_values
  SpecAssertionException : 1 specification step failed:
    And For X=4 and Y=4, the Sum should be 6 and the Product should be 8 => Sum: expected '6', got '8'
```

A CI job's output is captured, so the rendered specification isn't printed by default. Set
`BOBCAT_SPEC_CONSOLE=1` to print every specification at the end of the run anyway -- see
[Reading the specification the run produced](../marker-steps.md#reading-the-specification-the-run-produced).

## 5. Publish the run

A run can announce itself over a documented wire -- every scenario's start, verdict and timing --
which is how a build becomes something you can watch rather than something you read afterwards. The
receiving console is the [Bobcat UI](../bobcat-ui.md), `bobcat ui`; a run publishes to it when one
answers and skips publishing when none does, so a CI job with no UI running pays nothing for it.

<!-- JEREMY: a CI job could also start `bobcat ui --no-browser` itself and use `bobcat export-run`
or `bobcat failing-tests` at the end. I left that out rather than invent a recipe nobody has run. -->

Set `BOBCAT_RUN_TAG` in the pipeline so the evidence a run produces can be attributed back to the
commit or job that asked for it. Agents override this variable, so set it explicitly rather than
relying on a default.

The full vocabulary -- the events, the transport, the `BOBCAT_MONITOR*` variables and how to silence
the whole thing -- is in [What a Run Publishes](../monitor-design.md).

## 6. Decide about flakiness before it decides for you

The supervisor's retry budget lets a test pass on a second attempt and still exit 0, reported
separately so the pass-on-retry is visible rather than laundered:

```csharp
supervisor.RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 };
```

Use the budget to keep a pipeline moving, not to make a problem invisible -- the number you then watch
is the pass-on-retry count, not the pass count.

When flakiness is the actual problem rather than a nuisance, that is a bigger subject with its own
tutorial: [Reliable Integration Testing](reliable-integration-testing.md).

## 7. Segment a heavy pipeline

A large suite in one job hits timeouts that have nothing to do with your diff -- a loaded runner looks
exactly like a broken change. Segment the work into several jobs by class or trait before reaching for
more parallel lanes inside one job, and give each segment its own infrastructure.

If the suite is large enough that segmenting is not enough, the supervisor splits it across worker
processes -- again, [Reliable Integration Testing](reliable-integration-testing.md).

## Where to go next

- Making a large suite fast and trustworthy -- [Reliable Integration Testing](reliable-integration-testing.md)
- Failures an agent can act on -- [Agent Friendly Integration Tests](agent-friendly-tests.md)
