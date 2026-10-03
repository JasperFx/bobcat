# The Resident Runner

A resident runner is **a suite kept available to a monitor, running specifications when the monitor
asks**. It is the Bobcat half of interactive execution: somebody presses a button on a run console,
your suite runs the specifications they picked, and the results arrive on the ordinary run board as
an ordinary run.

```bash
./MySpecs --resident
```

```
Bobcat resident runner 7f3c… — MySpecs (gherkin), 41 specification(s). Waiting for commands.
```

The host never becomes a test host at all. `BOBCAT_RESIDENT=1` is the same request, for a parent
that cannot add an argument — a `dotnet watch` profile, or a container's entry point.

## The runner is a client, and that is the whole security model

It connects **out** to the same origin every publisher already probes (port 5525 by default) and
*asks* for work. A monitor can only answer a runner that asked, and can never make a runner do
anything. Nothing listens on a port, so there is nothing to secure.

A command names **specification identities and nothing else** — no arguments, no paths, no flags —
and everything it can name is something the runner already told the monitor it has. So the worst a
hostile monitor can do is ask for a test run.

The publisher's invariant applies unchanged: **a monitor that is absent, slow or hostile never
matters.**

- No monitor means an idle process that keeps asking on a *capped* backoff. It may come up later,
  and nothing will relaunch the runner just because it did.
- A dropped stream reconnects, resuming from `Last-Event-ID`.
- Anything unparseable — a proxy's HTML error page, a command type from a newer console — is
  **ignored**, not fatal. A resident runner that died of a network blip would be worse than one
  quietly waiting, because its parent only relaunches it on a source change.

## Both Gherkin entry points go resident

`--resident` is checked **before anything else is built**, in both of them:

| | |
|---|---|
| `BobcatTestApplication.Run` | the MTP host — including the [generated entry point](integrating-gherkin.md#dotnet-test), so a consumer with no `Main` of their own gets this for free |
| `BobcatRunner.Run` | the [command line runner](integrating-gherkin.md#command-line-runner), the JasperFx command family |

The check has to come before the parser in both, because `--resident` is not one of the command
family's options — and that is the bug it fixed. A suite written against `BobcatRunner` before the
MTP host existed read `--resident` as an unknown flag and ignored `BOBCAT_RESIDENT` entirely, so it
**ran all of its specs and exited 0** instead of going resident, and could not be driven from a
console's run buttons at all.

## One command at a time, refused rather than queued

The monitor owns the queue. A runner that silently queued would leave a person waiting on a run
whose turn they cannot see, so a second command arriving while one is in flight is rejected.

Every refusal carries both a human sentence and a machine word, because a command that is simply
never answered is indistinguishable from a runner that died:

| `refusal` | What it means |
|---|---|
| `busy` | **Not now.** A command is already in flight — *send it again* |
| `unknown-spec` | The command named a specification this runner does not have. Named, so the reason says which |
| `unsupported-mode` | The command asked for a mode this runner does not offer, or no longer offers. **Never silently downgraded** — someone who asked for warm and got cold would read the resulting wall clock as warm mode not working |
| `empty` | The command named no specification at all. Refused rather than read as "run everything", since the whole suite is what an ordinary run already does |

::: warning `run_finished` does not mean the runner is free
The run bracket closes *inside* the run, a hair before the in-flight slot clears. So a command sent
the instant `run_finished` arrives can still come back `busy`, and a client that wants a second run
**sends again** rather than assuming the first answer was yes.

That is the contract working, not a defect. It is invisible on a fast machine, which is exactly why
it is written down: Bobcat's own end-to-end test failed on CI three tags in a row over it, with the
product behaving as specified.
:::

The split between `busy` and the other three is the reason `refusal` exists at all. With only the
prose to go on, a monitor has to match on the *sentence* — so a rewording here would quietly turn
every busy into a hard rejection a person reads as a failed button. `refusal` is trailing and
optional, so a console that predates it goes on reading the reason.

## Cold and warm

**Cold is the default**: a fresh `BobcatRunner` per command. A command always runs the current code,
and a second cannot see the first's state because none of it survived.

**Warm** keeps one booted runner between commands, and is **opted into per command** — offered only
by a lane that can do it. What warmth buys is *only who pays for `StartAll`*: every selected
scenario still gets the full `ResetAll` → `BeginScenarioAll` → `EndScenarioAll` bracket, so **warm
never means dirty**.

- **Each command is its own run on the wire** — `run_started` … `run_finished`, its own run id,
  carrying its own command — so a viewer cannot tell a warm run from a cold one except by its speed.
  A warm *session* is not a run; a run is what a person asked for and what carries a verdict.
- **A cold command closes the warm session first.** They cannot coexist: a booted host holds the
  port, the database and the queues a second one would ask for, so "fresh everything" has to include
  tearing down what is up.
- **A failed reset withdraws warm, not the suite.** Any suite-level catastrophe in a warm selection
  marks the warm session damaged: the runner drops `warm` from its modes, **re-registers** so the
  monitor stops offering a button that will now be refused, and refuses a warm command *with that
  reason* — "warm is not a mode this runner offers", from a runner that was offering it a minute
  ago, explains nothing. **Cold is unaffected**, deliberately: a cold command starts over from
  exactly the thing that poisoned the warm host. It is never cleared — a broken session is broken,
  and a person who wants a working one asks for `restart`.

The narrower rule — only a reset that actually threw — would have to tell a broken resource from a
`SpecCatastrophicException` a step raised deliberately, and the cost of being wrong is asymmetric:
keeping a poisoned host on offer produces a run nobody can trust, while retiring a healthy one costs
a boot.

## `restart` means "exit so I can be relaunched"

It **cuts an in-flight run short** rather than waiting. A wedged run is the main reason someone
restarts a runner, so a restart that waited would be useless in exactly the case it exists for.

| Exit code | What it means to a parent |
|---|---|
| `75` (`EX_TEMPFAIL`) | a `restart` command — relaunch me |
| `0` | an orderly stop — Ctrl+C, SIGTERM, SIGQUIT |

Neither says anything about any test: the verdicts went out on the ingest stream as they happened,
so a parent deciding whether to relaunch never has to tell a red suite from a crashed runner. But it
does have to tell "relaunch me" from "I'm done", and **0 could not** — 0 is also what a
*non-resident* host returns after running its whole suite, so a parent relaunching on 0 would run
such a suite in a loop forever.

Signals go through `PosixSignalRegistration`, not `ProcessExit`. A `ProcessExit` handler runs after
the runner has returned and disposed the token source it would cancel, and the resulting
`ObjectDisposedException` aborts the process: **exit 134 from a runner that had done everything
right**.

## `BOBCAT_RUNNER_ID` — a parent owns the runner's identity

When it is set, it **is** the runner's id; otherwise the runner mints a GUID.

A resident runner lives under a watch and is relaunched on every source change, so a minted id meant
a *new runner per rebuild*. Three things followed from that, all of them bad: a command a person
pressed while the runner was rebuilding waited on an id that never came back, the monitor's picker
filled with dead runners, and a parent's status reports named an id the runner never registered
under. Handing the id over fixes all three, and re-registering is idempotent — which is what makes a
stable id safe. A monitor holding a stream open to a dead process of the same id simply has it
replaced by the live one's registration.

**One runner per checkout** is the rule that makes a command mean "run against the code in that
worktree", which is why the registration declares the repository and branch rather than leaving a
monitor to infer them from a run's events.

## A suite Bobcat does not own — `bobcat resident`

A Gherkin suite goes resident by being asked, because Bobcat owns that entry point. A **projected**
suite's entry point belongs to xUnit or TUnit, which will never learn what `--resident` means — so
the runner has to live outside the suite:

```bash
bobcat resident ./artifacts/MySpecs
bobcat resident ./artifacts/MySpecs --list        # what it specifies, registering with nothing
bobcat resident ./artifacts/MySpecs --url http://localhost:5525 --id my-checkout
```

It holds the suite's [spec manifest](spec-identities.md#listing-and-running-by-identity) and runs a
command by launching **the suite's own test host**, narrowed by that framework's filter. It ships
from the [`bobcat` tool](bobcat-tool.md) because the console at the other end references nothing in
this repository and so cannot be handed a class to host.

- **Cold only**, and that is measured rather than assumed. Microsoft's testing platform really does
  take repeated run requests in one live process; the blocker is *Bobcat's own run bracket*, which a
  projected suite opens on its first scenario and closes at process exit — so two run requests in
  one process yield one `run_started`, one run id, **no** `run_finished`, and the second command's
  scenarios landing on the first command's card. See
  [Warm runs for projected suites](warm-projected-runs.md).
- **A wiring mistake is refused at launch, not at the first button press**: a host that is not
  built, a suite that lists no specifications (usually a spec project with no runner adapter
  referenced), or a framework whose filter spelling Bobcat will not guess at. A runner that
  registered and then refused every command would look broken rather than unsupported.
- **TUnit is deliberately unsupported here.** It filters by tree-node path, and an unverified filter
  is exactly the run that looks filtered and is not.

The mechanism is lane-neutral — pointing it at a Gherkin host works and gets `--filter-uid` — but
that is still the wrong call for a Gherkin suite, because the in-process runner can offer warm and
this cannot: warmth means holding a booted host, and here the host is a child that exits. Pay a
process per command only when the lane leaves no alternative.

### What the child does *not* inherit

This is the substance of the out-of-process lane. Four variables a parent routinely sets break a
commanded run silently, so they are cleared for the child:

| | |
|---|---|
| `BOBCAT_RUN_ID` | would collapse every command into one ever-growing run card |
| `BOBCAT_RUN_OWNER` | would stop the child publishing a run bracket at all |
| `BOBCAT_LIST_SPECS` | would turn a run into a listing |
| `BOBCAT_RESIDENT` | would have the child register itself instead of running |

And two are forced on: `BOBCAT_MONITOR=1`, because a resident runner exists to serve a console and
`BOBCAT_MONITOR=0` is the kind of thing a CI job sets for a whole box; and `BOBCAT_MONITOR_URL` from
the runner's own `--url`, because a runner pointed at a second console would otherwise take that
console's commands and publish the runs to the default one.

## Attribution: a commanded run has no session

A run carrying a `Command` reports **no `Session` at all**. A resident runner started from an
agent's terminal inherits `CLAUDE_CODE_SESSION_ID` and holds it for its whole life, so without this
rule every run it made for a monitor command was stamped with the session that launched the
*runner* — and a console's agent page said "started by this session" about runs a person had pressed
in the UI.

`Command` is the true answer to who asked. `Tag` (`BOBCAT_RUN_TAG`) is unaffected, because it answers
a different question — what work the run speaks for — and a commanded re-run of a slice's specs is
still that slice's.

## The wire

CloudEvents in structured mode, hand-written, on the same origin as the run events. The shapes are
in [What a Run Publishes](monitor-design.md#the-resident-runner-wire-issue-390-built-2026-10-02), which is the
contract of record: a monitor reads these with no assembly reference to warn it, so the wire shape is
the contract and not an internal detail.

## See also

- [Checking Spec Identities](spec-identities.md) — the identity a command names, and how each lane runs one
- [What a Run Publishes](monitor-design.md) — the ingest wire the commanded run arrives on
- [Warm runs for projected suites](warm-projected-runs.md) — what warmth in the projected lane would cost
- [Integrating Bobcat Gherkin](integrating-gherkin.md) — the two entry points that answer `--resident`
