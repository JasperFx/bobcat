# Warm Runs for Projected Suites

A [resident runner](resident-runner.md) runs a command **cold** by default: a fresh test host per
command, narrowed to the specifications the command names. **Warm** keeps one test host open between
commands, so a second command is one more run request into a process that is already up.

```bash
bobcat resident ./artifacts/MySpecs     # registers both `cold` and `warm`
```

There is nothing to configure on the suite. `bobcat resident` offers both modes, and the console
chooses one per command. A runner never quietly downgrades: a warm command it cannot honour is
refused (`unsupported-mode`) with the reason, never run cold instead.

## What warmth buys, and what it does not

**It keeps the CLR warm: the JIT, the loaded assemblies and the code generation.** Measured against
`Bobcat.Xunit.Samples`, three run requests into one live host took **72ms, then 11ms, then 4ms** for
the same work, and two warm commands ran a whole test class in 2.9s where a cold launch per command
paid for a process each time.

**It does not keep your test framework's fixtures up.** xUnit's assembly and collection fixtures,
`IAsyncLifetime` and class fixtures are created and disposed *within* a run request, so a second
request pays for them again. For a suite whose cost is a collection fixture standing up a database,
warm saves the process and the JIT, not the boot. Measure it on your own suite before relying on it.

## Each command is still its own run

A warm command publishes its own `run_started` … `run_finished`, with its own run id and its own
command id, exactly as a cold one does, so the run board cannot tell them apart except by speed. The
adapters bracket a run per test **session** (a run request), not per process: `Bobcat.Xunit` through
a Microsoft.Testing.Platform session handler, `Bobcat.TUnit` through TUnit's own last-test-in-session
hook. Discovery opens no session, so listing a suite never adds an empty card to the board.

A warm child's environment is fixed when it starts, so the runner hands each command's id to it
through a file rather than a variable: `BOBCAT_RUN_COMMAND_FILE` names a path the runner rewrites
before every request, and it wins over `BOBCAT_RUN_COMMAND`. You never set it yourself; it is the
reason a warm run is attributed to the command that asked for it rather than to the first one.

## How a command finds its tests

A command names [specification identities](spec-identities.md), and a warm host runs tests by the
platform's own test uid. The runner joins the two through the suite's spec manifest:

- **xUnit v3:** the manifest's `Namespace.Class.method` against the display name the host reports at
  discovery.
- **TUnit:** the manifest's class and method against the location the host reports, so a
  data-driven method maps to each of its cases.

A uid matches exactly one test, so a warm command can run **any** set of identities, including sets
a cold TUnit command cannot express as one tree-node filter. An identity whose test cannot be found
is **refused by name** rather than dropped: a request for three specifications that silently ran two
would look exactly like a pass.

NUnit and MSTest suites are refused by `bobcat resident` at launch, warm or cold, until their filter
spelling is verified.

## When a warm session faults

- **Warm is withdrawn, not the suite.** The runner drops `warm` from its modes, re-registers so the
  console stops offering it, and refuses later warm commands saying why. Ask for a `restart` to get a
  fresh warm session.
- **Cold is unaffected**, since a cold command starts over from scratch anyway.
- **A cold command closes the warm session first.** A live host can hold the port, the database and
  the queues a second one would ask for.

## Background

The investigation behind warm mode, and the measurements, are in
[bobcat#394](https://github.com/JasperFx/bobcat/issues/394) and
[bobcat#402](https://github.com/JasperFx/bobcat/issues/402);
[`WarmProjectedRunTests`](https://github.com/JasperFx/bobcat/blob/main/src/Bobcat.Supervisor.Tests/WarmProjectedRunTests.cs)
pins the behaviour against a real host.
