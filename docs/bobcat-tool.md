# The `bobcat` Tool

A global .NET tool with two commands, and they have almost nothing to do with each other:

| | |
|---|---|
| **[`import-event-model`](#import-event-model)** | read and validate a curated **Event Model file**, or convert an eventmodelers.ai board export into that format |
| **[`resident`](#resident)** | keep a suite whose process Bobcat does not own available to a run console, running the specifications the console asks for |

Neither one *runs your suite the way you would*: `import-event-model` never loads a test assembly at
all, and `resident` launches the suite's own test host rather than hosting specs itself. Spec
projects are still driven by [`dotnet test` or the command line runner](integrating-gherkin.md).

```bash
dotnet tool install -g Bobcat.Console
```

`Bobcat.Console` is a plain command host: no web server, no store, nothing persisted between runs.
It carries the free, no-server half of the toolset.

::: tip The run console moved
The `bobcat` tool used to host the live run console. It does not any more — the board, the archive,
the model store and the design-time canvas all moved to
[Stoat](https://github.com/JasperFx/stoat) on 2026-09-18, because every one of them remembers
something across a process and this repository holds nothing that gates on a licence. Bobcat is the
format and the run. Runs still publish to that console exactly as before; see
[What a Run Publishes](monitor-design.md).
:::

## `import-event-model` {#import-event-model}

```bash
bobcat import-event-model Wallet.emodel.yaml
```

It sniffs the file and takes either shape.

### The curated format

A file with `schema` / `model` / `slices` is read and validated in place.

```
Model 'CritterCrush': 19 slice(s), 52 bound specification(s).
```

Warnings print whether or not it validated — a file carrying nothing but warnings **validates**,
which is exactly the silence the warning exists to break. Validation problems go to stderr and the
command fails:

```
The curated file did not validate:
  - not parseable as a curated event-model file: Exception during deserialization
```

### An emlang board export

An [eventmodelers.ai](https://eventmodelers.ai) board export is segmented into slices and **written
out as a curated file for you to review**, `<Model>.emodel.yaml` beside the input by default:

```bash
bobcat import-event-model board.yaml --model K9Crush
```

```
chapter 'TheSwiper': Command slice 'SwipeOnDog' triggered by 'Discovery Feed'.
chapter 'TheSwiper': Automation slice 'DetectMutualMatch' triggered by 'Dog Liked'.
chapter 'TheSwiper': View slice 'MatchList'.
chapter 'TheSwiper': View slice 'MatchList' consumes 3 event(s): DogLiked, DogPassed, MutualMatchDetected.
3 slice(s) from 1 chapter(s).
Curated model written to /path/to/K9Crush.emodel.yaml — review the segmentation there before building against it.
Model 'K9Crush': 3 slice(s), 1 bound specification(s).
```

**The segmentation is a set of reported guesses**, which is why it writes a file rather than acting
on what it inferred. A wrong guess should be a one-line diff in that file, not a re-import.

Either shape prints the model name, the slice count, and how many specifications are bound.

### Options

| Flag | What it does |
|---|---|
| `-m, --model <name>` | Model name for an emlang import; defaults to the file name. The curated format carries its own |
| `-n, --namespace <ns>` | Root namespace recorded for synthesized type names on an emlang import |
| `-o, --out <path>` | Where an emlang import writes the reviewable curated file |
| `-u, --url <base>` | Push the assembled model to a run console at this base URL |

#### `--url` takes the **base**, not the endpoint

It appends `/api/event-model` itself:

```bash
bobcat import-event-model board.yaml --url http://localhost:5525
```

```
Pushed to http://localhost:5525/api/event-model — open http://localhost:5525/event-model
```

Passing the endpoint gives you `…/api/event-model/api/event-model`, which fails somewhere less
obvious. An unreachable console is reported plainly:

```
Could not reach http://localhost:5999/api/event-model: Connection refused (localhost:5999)
```

## `resident`

```bash
bobcat resident ./artifacts/MySpecs
```

A [resident runner](resident-runner.md) for a suite Bobcat does not own the entry point of — which in
practice means a **projected** suite, whose `Main` belongs to xUnit or TUnit. A Gherkin suite goes
resident by being asked (`./MySpecs --resident`) and needs none of this.

It asks the host what it specifies, registers those identities with the console, and runs a command
by launching that host again narrowed to the specifications the command named.

| Flag | What it does |
|---|---|
| `-u, --url <base>` | the console to register with; defaults to the one every publisher probes. It travels down to the child run, so a runner pointed at a second console does not publish to the default one |
| `-i, --id <id>` | a stable runner id. Defaults to `BOBCAT_RUNNER_ID`, then to a fresh GUID |
| `--list` | print what the host specifies and exit, registering with nothing |

```
$ bobcat resident ./artifacts/MySpecs --list
Bobcat.Xunit.Samples (projected/xunit), 41 specification(s):
  Calculator/asserting values
  Calculator/bad values
  …
```

**It ships here because the console at the other end references nothing in this repository**, and so
cannot be handed a class to host. A wiring mistake is refused at launch rather than at the first
button press — a host that is not built, a suite that lists no specifications (usually a spec project
with no runner adapter referenced), or a framework whose filter spelling Bobcat will not guess at.
A runner that registered and then refused every command would look broken rather than unsupported.

It exits **75** after a `restart` command and **0** on an orderly stop, exactly as a resident spec
host does, so one parent can relaunch either on the same rule.

## Commands

```bash
bobcat help                        # list the commands
bobcat help import-event-model     # usage for one
```

The tool registers its commands explicitly rather than scanning, so nothing an assembly happens to
carry can join this surface. A bare `bobcat` prints usage and exits 1.

## Where this fits

The tool is the front door to the Event Modeling workflow: get a model in, review the segmentation,
then scaffold specs and build slice by slice. See
[Event Modeling and Spec Driven Development](tutorials/event-modeling.md) for the whole path, and
[Spec Identities](spec-identities.md) for the gate that keeps the model and the code honest.
