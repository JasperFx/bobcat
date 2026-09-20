# Integrating Bobcat with CI

**What you will build:** a pipeline step that runs your specs, fails the build for the right
reasons, and hands back a failure a human can act on without re-running anything locally.

## 1. Make the suite a test host

If your spec project references `Bobcat.Mtp`, you already have this: the generator emits a `Main`
and the project is a Microsoft.Testing.Platform host. `dotnet test` works, and so does running the
executable directly.

```bash
dotnet test                        # familiar, and what most pipelines already call
./MySpecs.Specs                    # the executable — per-test output that dotnet test hides
```

Prefer the executable in CI when you care about the output. `dotnet test` absorbs per-test stdout,
which is exactly the material you want when something fails on a machine you cannot attach to.

## 2. Understand the exit codes before you write the step

| Code | Meaning |
|---|---|
| `0` | Every scenario passed — a pass-on-retry still exits 0, and is reported separately |
| `1` | A regression failure, **or** a usage error including an unrecognized flag |
| `2` | Catastrophic — a resource failed to start, preflight failed, or no specs were discovered |

Two of these are worth designing around.

**`1` covers both a real failure and a typo in your own pipeline flags.** An unknown flag is an
error rather than being silently ignored, which is right, but it means a malformed CI invocation
looks like a failing suite until someone reads the output.

**`2` includes "no specs were discovered."** This is the one that protects you from the worst CI
outcome — a green pipeline over a suite that ran nothing. A `.feature` file that never became an
`<AdditionalFiles>` item, or a filter that matched nothing, exits 2 with an explanation rather than
0 with a smile. If an empty run is legitimately expected somewhere, set
`BobcatRunner.RequireSpecs = false` deliberately rather than discovering the default by accident.

## 3. Filter deliberately

```bash
./MySpecs.Specs run --feature "Checkout"      # case-insensitive substring of the feature title
./MySpecs.Specs run --tag smoke               # scenarios carrying a tag
```

Under `dotnet test` the flags differ — `--filter-feature`, `--filter-tag`, `--filter-uid` — because
the MTP host has its own argument surface and the two never mix. Write the tag without the `@`;
the platform consumes `@`-prefixed arguments as response files. Both filters intersect, and both
narrow `--list-tests` too. See [Integrating Bobcat Gherkin](../integrating-gherkin.md#dotnet-test).

Be careful here: on `list` and `preview` a filter that matches nothing prints nothing and exits 0.
Only `run` treats an empty result as a failure.

## 4. Get a machine-readable report

```bash
./MySpecs.Specs run --json
```

The JSON report carries the exit code, the counts, any discovery failure, and the features — which
is what you want feeding a build summary, a dashboard, or an agent. It replaces the console
rendering rather than accompanying it.

## 5. Publish the run

A run can announce itself over a documented wire — every scenario's start, verdict and timing —
which is how a build becomes something you can watch rather than something you read afterwards.
The receiving console lives in [Stoat](https://github.com/JasperFx/stoat); Bobcat publishes to it
and holds nothing itself.

Set `BOBCAT_RUN_TAG` in the pipeline so the evidence a run produces can be attributed back to the
commit or job that asked for it. Agents override this variable, so set it explicitly rather than
relying on a default.

The full vocabulary — the events, the transport, the `BOBCAT_MONITOR*` variables and how to silence
the whole thing — is in [What a Run Publishes](../monitor-design.md).

## 6. Decide about flakiness before it decides for you

A retry budget lets a scenario pass on a second attempt and still exit 0, reported separately so
the pass-on-retry is visible rather than laundered:

```csharp
[BobcatConfiguration]
public static void Configure(BobcatRunner runner)
{
    runner.RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 };
}
```

Use the budget to keep a pipeline moving, not to make a problem invisible — a retry budget masks
the failure count if you let it, and the number you then watch is the pass-on-retry count, not the
pass count.

When flakiness is the actual problem rather than a nuisance, that is a bigger subject with its own
tutorial: [Reliable Integration Testing](reliable-integration-testing.md).

## 7. Segment a heavy pipeline

A large suite in one job hits timeouts that have nothing to do with your diff — a loaded runner
looks exactly like a broken change. Segment the work into several jobs by feature or tag before
reaching for more parallel lanes inside one job, and give each segment its own infrastructure.

If the suite is large enough that segmenting is not enough, the supervisor splits it across worker
processes — again, [Reliable Integration Testing](reliable-integration-testing.md).

## Where to go next

- Making a large suite fast and trustworthy — [Reliable Integration Testing](reliable-integration-testing.md)
- Failures an agent can act on — [Agent Friendly Integration Tests](agent-friendly-tests.md)
