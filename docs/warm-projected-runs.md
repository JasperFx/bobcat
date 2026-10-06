# Warm runs for projected suites — findings

**Issue:** [#394](https://github.com/JasperFx/bobcat/issues/394) — an investigation, not a
commitment, opened alongside the resident runner ([#390](https://github.com/JasperFx/bobcat/issues/390))
and warm mode for Gherkin suites ([#393](https://github.com/JasperFx/bobcat/issues/393)).

**Date:** 2026-10-02.

**Verdict: usable with caveats — and the caveat is Bobcat's, not the platform's.**

Microsoft.Testing.Platform's server mode does everything the question needed it to do, on the
version src pins. What stood in the way was the shape of a *projected* suite's run bracket: it was
per **process**, and warm mode needs it per **command**.

> ## Update, 2026-10-06 — the bracket is fixed ([#402](https://github.com/JasperFx/bobcat/issues/402))
>
> **The blocker below is closed.** `Bobcat.Xunit` ships an `ITestSessionLifetimeHandler` that opens
> and closes the Bobcat run bracket per run **request**, and `MarkerStepRun.OpenRun` / `CloseRun`
> are the seam it drives. Two run requests in one live process now publish two `run_started`, two
> `RunId`s and two `run_finished`, with each command's scenarios under its own run. The two
> tripwires did their job and are retired; `WarmProjectedRunTests` asserts the fixed behaviour, and
> removing the registration is enough to make it red again (checked).
>
> **And the lane runs warm.** `WarmProjectedResidentSuite` holds the suite's test host open in
> server mode; `bobcat resident` registers `cold` and `warm`, and a console chooses per command.
> Two warm commands in one live host measured **2.9s for the whole test class** against a cold
> launch per command. See "Warm, as built" below.

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

## The blocker, as measured in 2026-10 — **now fixed, see the update above**

## The run bracket was per process

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

### What closing it took — all three, and what each turned out to be

A per-request bracket in `MarkerStepRun`: the latch (`_started`, `_info`, `_sink`) is now something
a caller opens and closes rather than something the first scenario opens and `ProcessExit` closes.
The three complications were real, and each resolved differently from the guess:

1. **Nothing in the projected lane knew when a run request begins or ends** — the adapter sees
   tests, not requests. `ITestSessionLifetimeHandler` is the platform's hook, and **a session
   really is a request**: measured on 1.9.1, three `testing/runTests` requests into one live
   process fire it three times with three distinct `SessionUid`s, each properly bracketed; a
   one-shot direct run fires it once; and **discovery opens no session at all**, which is what
   keeps `--list-tests` from putting an empty card on the board.

   It is registered half in C# and half in MSBuild — the platform generates a
   `SelfRegisteredExtensions` class calling `AddExtensions` on every type a
   `TestingPlatformBuilderHook` item names — so `Bobcat.Xunit` ships
   `buildTransitive/Bobcat.Xunit.props`, and in-repo projects declare the item themselves because
   a `ProjectReference` takes no build assets.

   **The dependency decision went as the finding predicted, and `Bobcat.TUnit` does not follow.**
   `Bobcat.Xunit` now references `Microsoft.Testing.Platform`, which costs its consumers nothing
   (a consumer is by definition an xUnit v3 MTP host and already resolves it through `xunit.v3`).
   `TUnit.Core` depends on no test platform at all — that is what makes *that* package safe to ship
   beside a pinned 1.9.1, since `TUnit.Engine` wants 2.4.0 — so it keeps that property and keeps
   the per-process bracket. Nothing regresses: the backstop below is still its whole bracket. The
   reasoning is written into `Bobcat.TUnit.csproj`, where someone changing it will read it.
2. **`ProcessExit` stays, and is now explicitly the backstop.** `CloseRun` is idempotent, so when
   the session hook already closed the bracket the backstop finds nothing to close. One thing had
   to change for that to be safe: it now **drains the publisher unconditionally**. The old code
   returned early when it had no bracket to close, which after this refactor would have let the
   last `run_finished` of every warm process die in the channel.
3. **The command id is read per request** — `OpenRun` re-runs `MonitorRunInfo.Discover`, so a
   process serving several commands reads the current `BOBCAT_RUN_COMMAND` each time instead of
   stamping every run with the first. The sink is deliberately *not* re-resolved: that is a process
   fact, and probing 5525 per request would charge every command for a console handshake.

   **But for a warm child the variable cannot change, and that is the finding this uncovered.** A
   child's environment is fixed at launch, and MTP 1.9.1 offers no per-request metadata slot: the
   `runId` on `testing/runTests` is the client's own and **is not** the `SessionUid` the handler
   receives (measured — they are unrelated GUIDs). So "read per request" is now true, and in the
   out-of-process lane there is still nothing new to read.

## Warm, as built

`WarmProjectedResidentSuite` (in **`Bobcat.Supervisor`**) wraps `OutOfProcessResidentSuite`: cold is
delegated to it unchanged, and warm holds an `MtpWorkerClient` open across commands. The three
things the finding above said were owed resolved like this:

### 1. The per-request command channel — `BOBCAT_RUN_COMMAND_FILE`

A warm child's environment is **fixed at launch**, so `BOBCAT_RUN_COMMAND` cannot serve: every
command after the first would be stamped with the first one's id, which is exactly the
mis-attribution [#401](https://github.com/JasperFx/bobcat/issues/401) was opened to fix. Nor is
there a slot in the protocol — measured on 1.9.1, the `runId` a client sends on
`testing/runTests` is the client's own and **is not** the `SessionUid` the session handler receives.

So: a path fixed at launch, whose contents the parent rewrites **before** each request and the
child reads when its session opens. It is the exact inverse of `BOBCAT_LIST_SPECS`, where a path is
fixed at launch and the *child* writes what the *parent* reads. No race arises, because a resident
runner runs one command at a time and refuses rather than queues.

It **wins over `BOBCAT_RUN_COMMAND`**, being the narrower claim, and every failure to read it falls
back to the variable and then to null — run attribution must never be able to fail a run.
`OutOfProcessResidentSuite.EnvironmentFor` **clears** it for a cold child, alongside the four it
already clears: a parent's stale file must never be read as this command.

### 2. Layering: the warm suite moved up, the client did not move down

`MtpWorkerClient` stays in `Bobcat.Supervisor`. Moving it down into core was the alternative and
was refused: **core is what every spec project references**, so putting an MTP JSON-RPC client
there would tax all of them for a capability only the resident tool uses. `IWorkerClient` is
documented as *the* seam for exactly this, and this issue anticipated "a second caller of
`IWorkerClient` rather than a new transport" — so the second caller belongs on the supervisor's
side of it. `Bobcat.Console` → `Bobcat.Supervisor` is honest: driving a test host as a process is
that tool's whole job in this lane.

### 3. The identity → uid join

One lookup, as Q3 established: the manifest's `TestClass` + `TestMethod` (its `QualifiedTestMethod`)
against the discovery display name. A nested class is `Ns.Outer+Inner` in the manifest and may be
dotted in a display name, so that is tried as a *fallback* after the exact match this file verified.
An identity whose uid cannot be named is **refused**, not dropped from the subset — a request for
three specs that silently ran two looks exactly like a pass.

### What warmth buys here, and what it does not

It keeps the **CLR** warm: the JIT, the loaded assemblies, the codegen — the 72ms → 11ms → 4ms
above. It does **not** keep the suite's own fixtures up, because xUnit's assembly and collection
fixtures are created and disposed *within* a run request. That is the opposite of the Gherkin lane,
where what warms is Bobcat's own `TestResources.StartAll`. So the Q2 caveat stands word for word
and is worth repeating to anyone choosing a mode: for a suite whose database lives in a collection
fixture, warm saves the process and the JIT, not the boot.

### Withdrawal, following the Gherkin lane's rule

A warm session that faults takes `warm` off the registered modes, re-registers so the monitor stops
offering a button that would now be refused, and refuses a warm command **with that reason** —
because "warm is not a mode this runner offers", from a runner that was offering it a minute ago,
explains nothing. **Cold is unaffected**: a cold command starts over from exactly the thing that
poisoned the warm host. And a cold command **closes the warm session first**, since a booted host
holds the port, the database and the queues a second one would ask for.

## Decision

**The projected lane runs warm**, as of #402. `BobcatResidentSuite` offers `cold` and `warm`
in process; `WarmProjectedResidentSuite` offers both out of process, delegating cold to
`OutOfProcessResidentSuite` (which itself still offers `cold` alone — it is in core, and warmth
needs the supervisor's client). `ResidentRunner` still refuses a `warm` command a suite never
registered rather than quietly downgrading it, which is what makes the withdrawal above safe.

Cold, note, needed none of what is owed below: a command launches the suite's own host with
`SpecFilterArguments.For`'s filter, that host opens and closes exactly one run bracket because it
is exactly one process, and the whole blocker measured here is about a *second* request arriving in
a process that already published `run_started`. The price of being cold is one process per command,
which is the thing warmth would buy back.

Nothing is owed here any more. The one thing worth measuring next is whether warmth is worth asking
for on a real suite: it saves the process and the JIT, not the boot, so a suite whose cost is a
collection fixture standing up a database will see less of a difference than the 72ms → 4ms above
suggests.

## Reproduce

```bash
dotnet build bobcat.slnx
export TESTINGPLATFORM_TELEMETRY_OPTOUT=1
./src/Bobcat.Supervisor.Tests/bin/Debug/net10.0/Bobcat.Supervisor.Tests \
  --filter-class "*WarmProjectedRunTests"
```
