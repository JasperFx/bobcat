# The Resident Runner

A resident runner is **a suite kept available to a monitor, running specifications when the monitor
asks**. It is the Bobcat half of interactive execution: somebody presses a button on a run console,
your suite runs the specifications they picked, and the results arrive on the ordinary run board as
an ordinary run.

## What this is for

A run console shows a suite's specifications, and sooner or later somebody looking at a red one wants
to run it again *from there* -- after a fix, against the code in a particular worktree, without
switching to a terminal and working out the filter. The resident runner is what makes that button
work. It sits beside your suite, tells the console what the suite specifies, and when the console
sends it identities, runs exactly those and nothing else.

Two consoles drive it today. [`bobcat ui`](bobcat-ui.md#run-buttons) has **Run again**, **Re-run
failed**, **Run *feature***, **Run this slice** on the canvas, and **Run all**, plus a page per runner
listing every specification it can run with the steps each one plans; every one of those needs a
resident runner attached for the suite. [Stoat](https://github.com/JasperFx/stoat), the Critter Stack's
agent coordination tool, was the original consumer: `stoat runner` keeps a `bobcat resident` under a
file watch and relaunches it on every rebuild, so that an agent's or a person's "run this slice's
specs" always runs against the current code of that checkout. The stable runner id, the restart exit
code and the one-runner-per-checkout rule below all come from that loop.

It is a separate process rather than a mode of the suite because a spec project's entry point belongs
to its test framework, which will never learn what "go resident" means. So the runner lives in the
[`bobcat` tool](bobcat-tool.md#resident), reads the suite's manifest, and runs a command by launching
the suite's own test host narrowed by that framework's filter. The
[specification identity](spec-identities.md) is the one language the button, the runner and the
filter share.

## Starting one

```bash
bobcat resident ./artifacts/MySpecs
bobcat resident ./artifacts/MySpecs --list        # what it specifies, registering with nothing
bobcat resident ./artifacts/MySpecs --url http://localhost:5525 --id my-checkout

# Commands from the first console; a commanded run published to both
bobcat resident ./artifacts/MySpecs --url http://localhost:5525,http://localhost:5050

# The build output folder or the .csproj name the same host; nothing is built
bobcat resident src/MySpecs/bin/Debug/net10.0
bobcat resident src/MySpecs/MySpecs.csproj --configuration Release
```

The path is the suite's test host: its executable, the build output folder holding it, the `.csproj`,
or the directory holding that `.csproj`. A build output folder that also holds the application the
specs test (a spec project referencing a web app has both executables) resolves to the test host: the
one the folder's own project builds, else the only one carrying a test framework. Only a folder still
ambiguous after that is refused, with the candidates named
([bobcat#797](https://github.com/JasperFx/bobcat/issues/797)). A project is resolved to the host MSBuild says it builds for
`--framework` (the newest it targets by default) and `--configuration` (`Debug` by default), and one
whose build is not there is refused with the path it looked at -- the runner never builds, because
whatever relaunches it on a source change is what built the suite.

`--list` prints what the runner would register, one identity per line. Against this repository's
sample corpus, trimmed:

```
Bobcat.Xunit.Samples (projected/xunit), 95 specification(s):
  Acceptance specifications/a wrong is gathered before the behaviour is finished
  Acceptance specifications/an acceptance spec that passes is ready to promote
  Acceptance specifications/the behaviour throws because it does not exist yet
  Actions/custom logging from a step
  Actions/explicit action
  Actions/implicit action
  Actions/off stage setup never ran
  Alpha transport/each subclass is its own feature
  …
  Withdrawals/the overdraft boundary
  Withdrawals/the overdraft boundary with a wrong row
```

The runner reads the suite's [spec manifest](spec-identities.md#listing-and-running-by-identity),
registers those identities with the console, and runs a command by driving **the suite's own test
host**, narrowed by that framework's filter. It ships from the tool because the console at the other
end references nothing else in this repository and so cannot be handed a class to host.

The registration also carries each specification's **planned steps** (its marker comments and step
calls, as `bobcat preview` shows them), read from the same listing run at its exit
(`BOBCAT_LIST_OUTLINE`). That is what lets a console list a suite's specifications before any of them
has run -- `bobcat ui`'s runner page. A runner from an older Bobcat registers identities only.

**A wiring mistake is refused at launch, not at the first button press**: a host that is not built, a
suite that lists no specifications (usually a spec project with no runner adapter referenced), or a
framework whose filter spelling Bobcat will not guess at. A runner that registered and then refused
every command would look broken rather than unsupported.

**xUnit v3 and TUnit.** A TUnit suite is filtered by tree-node path, which can name several
methods of one class or one method name across classes in a cold command, but not an arbitrary set;
a warm command runs any set ([#470](https://github.com/JasperFx/bobcat/issues/470)). NUnit and MSTest
are refused until their filter spelling is pinned against a real host. See
[Spec Identities](spec-identities.md#the-translation-and-the-one-thing-it-refuses).

## The runner is a client, and that is the whole security model

It connects **out** to one origin -- `--url`'s first entry, else `BOBCAT_MONITOR_URL`'s first, else
port 5525 -- and *asks* for work. A monitor can only answer a runner that asked, and can never make a runner do
anything. Nothing listens on a port, so there is nothing to secure.

A command names **specification identities and nothing else** -- no arguments, no paths, no flags --
and everything it can name is something the runner already told the monitor it has. So the worst a
hostile monitor can do is ask for a test run.

The publisher's invariant applies unchanged: **a monitor that is absent, slow or hostile never
matters.**

- No monitor means an idle process that keeps asking on a *capped* backoff. It may come up later,
  and nothing will relaunch the runner just because it did.
- A dropped stream reconnects, resuming from `Last-Event-ID`.
- Anything unparseable -- a proxy's HTML error page, a command type from a newer console -- is
  **ignored**, not fatal. A resident runner that died of a network blip would be worse than one
  quietly waiting, because its parent only relaunches it on a source change.

## One command at a time, refused rather than queued

The monitor owns the queue. A runner that silently queued would leave a person waiting on a run
whose turn they cannot see, so a second command arriving while one is in flight is rejected.

Every refusal carries both a human sentence and a machine word, because a command that is simply
never answered is indistinguishable from a runner that died:

| `refusal` | What it means |
|---|---|
| `busy` | **Not now.** A command is already in flight -- *send it again* |
| `unknown-spec` | The command named a specification this runner does not have. Named, so the reason says which |
| `unsupported-mode` | The command asked for a mode this runner does not offer, or no longer offers. **Never silently downgraded** -- someone who asked for warm and got cold would read the resulting wall clock as warm mode not working |
| `empty` | The command named no specification at all. Refused rather than read as "run everything", since the whole suite is what an ordinary run already does |

::: warning `run_finished` does not mean the runner is free
The run bracket closes *inside* the run, a hair before the in-flight slot clears. So a command sent
the instant `run_finished` arrives can still come back `busy`, and a client that wants a second run
**sends again** rather than assuming the first answer was yes.

That is the contract working, not a defect, and it is easy to miss on a fast machine.
:::

The split between `busy` and the other three is the reason `refusal` exists at all. With only the
prose to go on, a monitor has to match on the *sentence* -- so a rewording here would quietly turn
every busy into a hard rejection a person reads as a failed button. `refusal` is trailing and
optional, so a console that predates it goes on reading the reason.

## Cold and warm

**Cold is the default**: every command launches the suite's test host as a fresh process, narrowed to
the specifications it named. A command always runs the current code, and a second cannot see the
first's state because none of it survived.

**Warm** keeps one test host open in Microsoft.Testing.Platform's server mode between commands, and is
**opted into per command** -- `bobcat resident` registers both `cold` and `warm`, and a console chooses.
Each command is still its own run on the wire -- `run_started` … `run_finished`, its own run id,
carrying its own command -- so a viewer cannot tell a warm run from a cold one except by its speed.

**What warmth buys is the CLR, not your fixtures.** It keeps the JIT, the loaded assemblies and the
code generation warm -- measured at 72ms, then 11ms, then 4ms for the same request. It does **not**
keep your test framework's fixtures up: xUnit's assembly and collection fixtures are created and
disposed *within* a run request. So for a suite whose cost is a collection fixture standing up a
database, warm saves the process and the JIT, not the boot. The measurements and the design are in
[Warm runs for projected suites](warm-projected-runs.md).

- **A cold command closes the warm session first.** They cannot coexist: a live host can hold the
  port, the database and the queues a second one would ask for, so "fresh everything" has to include
  tearing down what is up.
- **A faulted warm session withdraws warm, not the suite.** The runner drops `warm` from its modes,
  **re-registers** so the monitor stops offering a button that will now be refused, and refuses a warm
  command *with that reason* -- "warm is not a mode this runner offers", from a runner that was offering
  it a minute ago, explains nothing. **Cold is unaffected**, deliberately: a cold command starts over
  from exactly the thing that poisoned the warm host. A person who wants a working warm session asks
  for `restart`.

## `restart` means "start me again"

It **cuts an in-flight run short** rather than waiting. A wedged run is the main reason someone
restarts a runner, so a restart that waited would be useless in exactly the case it exists for.

**Who does the starting again depends on whether anything is above the runner**
([bobcat#796](https://github.com/JasperFx/bobcat/issues/796)):

- **`bobcat resident <host>` on its own** restarts **in place**: it reloads the suite, so a rebuilt
  host's specifications are picked up, and registers again under the same runner id. Exiting there
  would leave nothing to relaunch it, and the UI's "restart" would quietly mean "kill".
- **Under a parent that relaunches it** (`stoat runner`, a container supervisor), the parent declares
  itself with `--supervised` or `BOBCAT_SUPERVISED=1`, and the runner **exits** with the codes below.
  A resident spec host (`--resident` / `BOBCAT_RESIDENT=1`) always exits this way, because its
  process *is* the suite and only something outside it can start it again.

| Exit code | What it means to a parent |
|---|---|
| `75` (`EX_TEMPFAIL`) | a `restart` command -- relaunch me |
| `0` | an orderly stop -- Ctrl+C, SIGTERM, SIGQUIT |

Neither says anything about any test: the verdicts went out on the ingest stream as they happened,
so a parent deciding whether to relaunch never has to tell a red suite from a crashed runner. But it
does have to tell "relaunch me" from "I'm done", and **0 could not** -- 0 is also what a
test host returns after running its whole suite, so a parent relaunching on 0 would run
such a suite in a loop forever.

## `BOBCAT_RUNNER_ID` -- a parent owns the runner's identity

When it is set (or `--id` is passed), it **is** the runner's id; otherwise the runner mints a GUID.

A resident runner lives under a watch and is relaunched on every source change, so a minted id meant
a *new runner per rebuild*. Three things followed from that, all of them bad: a command a person
pressed while the runner was rebuilding waited on an id that never came back, the monitor's picker
filled with dead runners, and a parent's status reports named an id the runner never registered
under. Handing the id over fixes all three, and re-registering is idempotent -- which is what makes a
stable id safe.

**One runner per checkout** is the rule that makes a command mean "run against the code in that
worktree", which is why the registration declares the repository and branch rather than leaving a
monitor to infer them from a run's events.

## What the child does *not* inherit

Five variables a parent routinely sets break a commanded run silently, so they are cleared for the
child:

| | |
|---|---|
| `BOBCAT_RUN_ID` | would collapse every command into one ever-growing run card |
| `BOBCAT_RUN_OWNER` | would stop the child publishing a run bracket at all |
| `BOBCAT_LIST_SPECS` | would turn a run into a listing |
| `BOBCAT_RESIDENT` | would have the child register itself instead of running |
| `BOBCAT_RUN_COMMAND_FILE` | a parent's stale command file would be read as this command (a warm session writes its own, see [Warm Runs](warm-projected-runs.md)) |

And two are forced on: `BOBCAT_MONITOR=1`, because a resident runner exists to serve a console and
`BOBCAT_MONITOR=0` is the kind of thing a CI job sets for a whole box (an explicit `1` also tells
the child's probe a console is expected, so it waits up to 5s rather than 250ms for the answer, which
a loaded CI machine can need); and `BOBCAT_MONITOR_URL` from
the runner's own `--url`, because a runner pointed at a second console would otherwise take that
console's commands and publish the runs to the default one.

**The URL travels as a list, and taking commands does not.** `--url` accepts the same comma- or
semicolon-separated list `BOBCAT_MONITOR_URL` takes ([bobcat#530](https://github.com/JasperFx/bobcat/issues/530)),
and the whole list reaches the child -- so a commanded run shows up on every console watching. The
runner itself registers with and reads its command stream from **one** console, the first entry:
commands come from somewhere in particular, while a run may be watched from anywhere.

## Attribution: a commanded run has no session

A run carrying a `Command` reports **no `Session` at all**. A resident runner started from an
agent's terminal inherits `CLAUDE_CODE_SESSION_ID` and holds it for its whole life, so without this
rule every run it made for a monitor command was stamped with the session that launched the
*runner* -- and a console's agent page said "started by this session" about runs a person had pressed
in the UI.

`Command` is the true answer to who asked. `Tag` (`BOBCAT_RUN_TAG`) is unaffected, because it answers
a different question -- what work the run speaks for -- and a commanded re-run of a slice's specs is
still that slice's.

## The wire

CloudEvents in structured mode, hand-written, on the same origin as the run events. The shapes are
in [What a Run Publishes](monitor-design.md#the-resident-runner-wire-issue-390-built-2026-10-02), which is the
contract of record: a monitor reads these with no assembly reference to warn it, so the wire shape is
the contract and not an internal detail.

## See also

- [Checking Spec Identities](spec-identities.md) -- the identity a command names, and how it becomes a framework filter
- [What a Run Publishes](monitor-design.md) -- the ingest wire the commanded run arrives on
- [Warm runs for projected suites](warm-projected-runs.md) -- what warmth costs and buys, measured
- [The `bobcat` Tool](bobcat-tool.md#resident) -- the command and its flags
