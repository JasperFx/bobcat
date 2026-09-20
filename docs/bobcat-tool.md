# The `bobcat` Tool

A global .NET tool for **Event Model files** — reading them, validating them, and converting a
board export into the curated format.

It has nothing to do with running specs. Spec projects are driven either by
[`dotnet test` or the command line runner](integrating-gherkin.md); this tool never loads
your test assembly and never executes a scenario. The shared name is the only thing they have in
common.

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

## `import-event-model`

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

## Commands

```bash
bobcat help                        # list the commands
bobcat help import-event-model     # usage for one
```

`import-event-model` is the command this tool exists for. `bobcat help` will also list a number of
commands inherited from the JasperFx command family — `projections`, `event-query`, `codegen`,
`describe`, `resources` and others — which are meaningful only inside a configured application and
do nothing useful here.

::: warning Always name a command
`bobcat` with no arguments does not print help. It falls through to the inherited `run` command,
which starts a host and blocks until interrupted — in a pipeline, that hangs the job rather than
failing it. Use `bobcat help` to see the commands, and always pass one.
:::

## Where this fits

The tool is the front door to the Event Modeling workflow: get a model in, review the segmentation,
then scaffold specs and build slice by slice. See
[Event Modeling and Spec Driven Development](tutorials/event-modeling.md) for the whole path, and
[Checking Spec Identities Against the Model](spec-identities.md) for the gate that keeps the model
and the code honest.
