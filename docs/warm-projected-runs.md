# Warm runs for projected suites — findings

**Issue:** [#394](https://github.com/JasperFx/bobcat/issues/394) — an investigation, not a
commitment, opened alongside the resident runner ([#390](https://github.com/JasperFx/bobcat/issues/390))
and warm mode for Gherkin suites ([#393](https://github.com/JasperFx/bobcat/issues/393)).

**Date:** 2026-10-02.

**Verdict: usable with caveats — and the caveat is Bobcat's, not the platform's.**

Microsoft.Testing.Platform's server mode does everything the question needed it to do, on the
version src pins. What stands in the way is the shape of a *projected* suite's run bracket: it is
per **process**, and warm mode needs it per **command**. So the projected lane stays cold-only in
the resident runner, and the work to change that is a day inside `MarkerStepRun`, not an
investigation into somebody else's protocol.

Everything below was measured, not read from docs. The measurements are
`src/Bobcat.Supervisor.Tests/WarmProjectedRunTests.cs` — four tests, two of them **tripwires that
pin the broken behaviour on purpose**, so that whoever fixes the bracket is told the blocker is
gone rather than finding this file stale.

> Measured against **Microsoft.Testing.Platform 1.9.1**, the version `src` pins, driving
> `Bobcat.Xunit.Samples` (xUnit v3). Deliberately *not* the `spikes/mtp-orchestration` harness,
> whose `Directory.Packages.props` is on MTP 2.x — the right harness for issue #43 and exactly the
> wrong version for this question.

---

## The three questions, answered

### Q1. Is server mode usable from a plain process, on 1.9.1?

**Yes, and it already is.** `MtpWorkerClient` has driven it for every supervised run since #41:
`--server jsonrpc --client-port <n>`, the host dials back, `initialize`, then
`testing/discoverTests` and `testing/runTests`. Nothing about the resident runner needs a new
transport — `IWorkerClient` is the seam and this would be a second caller of it.

The #43 spike's warning stands unchanged and is the reason that seam exists: server mode is
undocumented and has moved between versions. That is an argument for keeping it behind
`IWorkerClient`, not against using it.

### Q2. Does it keep the host booted between run requests?

**Yes, at the process level.** Three `testing/runTests` requests in one live process, each
answering with exactly the test it asked for, no fault. The cost profile makes the point on its
own — **72ms, then 11ms, then 4ms** for the same work. Nothing is consumed by being asked for
once; the third request repeats the first.

This is not really a discovery. The supervisor's same-process retry already depends on it: a
retry returns to the lane the test ran in, which means a second run request into a process that
has already run one.

**But "the host is booted" is the wrong level for a projected suite.** What a Gherkin suite warms
is `TestResources.StartAll` — Bobcat's own, and Bobcat keeps it. What a projected suite warms is
whatever its *test framework* owns: xUnit's assembly and collection fixtures, `IAsyncLifetime`,
anything a `ClassFixture` stands up. Those are created and disposed within a run request, so a
second request pays for them again. Server mode keeps the CLR warm — the JIT, the loaded
assemblies, the codegen — which is worth real time, but it is not what "skip the boot" means for a
suite whose database lives in a collection fixture.

### Q3. Can a run request be filtered to the identities #391 maps?

**Yes, with a one-line join.** Server mode filters by the platform's `tests: [{uid, …}]`, and an
xUnit v3 uid is an opaque hash — nothing a monitor could know. But discovery reports the display
name as **`Namespace.Class.method`**, which is exactly what #391's manifest spells as `TestClass`
plus `TestMethod`. So:

```
identity → manifest → "Ns.Class.method" → discovered display name → platform uid
```

No new mechanism, and the manifest is already written at discovery time by the generated module
initializer. Worth noting that this is a *better* filter than the cold path's `--filter-method`:
a uid cannot match more than one test, while a method name can.

And `GuardAgainstAnUnfilteredRun` applies here as it does everywhere — the platform silently
ignores a subset parameter it does not understand and runs the whole suite.

---

## The blocker: the run bracket is per process

A projected suite opens its run on the **first scenario** of the process and closes it from a
**`ProcessExit` handler** (`MarkerStepRun.ensureStarted` / `finish`). In a one-shot `dotnet test`
process that is exactly right: one process, one run.

In a live server-mode process it is wrong in three ways at once. Measured, over two run requests:

| What the wire should carry | What it carried |
|---|---|
| Two `run_started`, two `RunId`s | **One** `run_started`, one `RunId` |
| Two `run_finished` | **None** |
| Each command's scenarios under its own run | Both commands' `scenario_finished` under the first run |

So the second command's results **append to the first command's card**, and no run ever closes
while the process lives. On a board that is not a cosmetic problem: a run with no `run_finished`
is indistinguishable from a wedged one, which is precisely the failure
[#195](https://github.com/JasperFx/bobcat/issues/195) was opened for.

Issue #393 requires the opposite, in as many words: *each command is its own run on the wire, so a
viewer cannot tell a warm run from a cold one except by its speed.* The Gherkin lane satisfies
that because `BobcatRunner.RunWarmSelection` attaches and detaches a monitor publisher **per
selection**. The projected lane has nothing to attach it to.

### What closing it would take

A per-request bracket in `MarkerStepRun` — the latch (`_started`, `_info`, `_sink`) becomes
something a caller can open and close rather than something the first scenario opens and
`ProcessExit` closes. Three things make that less simple than it sounds, and all three are
reasons to do it deliberately rather than as part of #390:

1. **Nothing in the projected lane knows when a run request begins or ends.** The adapter sees
   tests, not requests. An MTP extension — a `ITestSessionLifetimeHandler`, which is the hook the
   platform has for exactly this — would have to be registered by `Bobcat.Xunit` / `Bobcat.TUnit`
   and would be the first platform extension either package ships. That is a real dependency
   decision: `Bobcat.TUnit` deliberately references only `TUnit.Core`, which depends on no test
   platform at all, and that is what makes it safe to ship.
2. **`ProcessExit` has to stay** for the one-shot case, so the bracket needs to be idempotent and
   know whether anyone already closed it.
3. **The command id has to reach it.** A resident runner holds the command, and in this lane the
   runner is a *different process* — so `BOBCAT_RUN_COMMAND` is the channel, which is one of the
   two cases it was built for (#392). But it is a process-wide variable, and a warm process takes
   many commands, so it would have to be read per request instead of at `Discover` time.

None of that is hard. It is simply not free, and it buys a lane that is already served correctly
by cold runs.

---

## Decision

**The projected lane stays cold-only in the resident runner.** `BobcatResidentSuite` offers
`cold` and `warm`; a projected suite would offer `cold` alone, and `ResidentRunner` refuses a
`warm` command it never registered rather than quietly downgrading it.

Reopen this when someone has a projected suite whose boot is expensive enough to pay for the
bracket work — a collection fixture standing up a real database is the shape to look for. The
platform half is proved; only Bobcat's half is owed.

## Reproduce

```bash
dotnet build bobcat.slnx
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
./src/Bobcat.Supervisor.Tests/bin/Debug/net10.0/Bobcat.Supervisor.Tests \
  --filter-class "*WarmProjectedRunTests"
```
