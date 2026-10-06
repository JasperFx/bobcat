# The `bobcat` Tool

A global .NET tool with two commands, and they have almost nothing to do with each other:

| | |
|---|---|
| **[`import-event-model`](#import-event-model)** | convert an eventmodelers.ai board export into **C#** — stub records plus one `EventModelDefinition` |
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
bobcat import-event-model board.yaml --model K9Crush
```

Reads an [eventmodelers.ai](https://eventmodelers.ai) board export, segments it into slices, and
writes the design out as **C#**:

```
chapter 'TheSwiper': Command slice 'SwipeOnDog' triggered by 'Discovery Feed'.
chapter 'TheSwiper': Automation slice 'DetectMutualMatch' triggered by 'Dog Liked'.
chapter 'TheSwiper': View slice 'MatchList'.
chapter 'TheSwiper': View slice 'MatchList' consumes 3 event(s): DogLiked, DogPassed, MutualMatchDetected.
3 slice(s) from 1 chapter(s).
Wrote 6 stub record(s) to ./K9CrushStubs.cs and the event model to ./K9Crush.cs. Nothing
regenerates either file — the segmentation above is a set of guesses, so correct one with an edit
rather than a re-import.
Model 'K9Crush': 3 slice(s), 1 bound specification(s).
```

### Two files: the stubs and the definition

`<Model>Stubs.cs` is one field-less record per command, event, aggregate and view the board named:

```csharp
namespace K9Crush;

/// <summary>
/// emitted by SwipeOnDog.
/// consumed by MatchList.
/// </summary>
public record DogLiked;
```

**Field-less is what a board can honestly produce.** An emlang export carries no field information
at all — the board's props are intentionally omitted — so a name is the whole truth it can tell.
Inventing an `Id` would be a guess with no basis that you would then have to un-guess.

`<Model>.cs` is one `EventModelDefinition` declaring the slices through the JasperFx.Events fluent
API, against those stubs:

```csharp
public class K9CrushEventModel : EventModelDefinition
{
    public override string Name => "K9Crush";

    public override void Configure(EventModelBuilder model)
    {
        model.Command<SwipeOnDog>()
            .InChapter("TheSwiper")
            .TriggeredBy("Discovery Feed", TriggerKind.Human)
            .Emits<DogLiked>()
            .Emits<DogPassed>()
            .LinksToSpecification("SwipeOnDog/ALikeIsRecorded");

        model.Automation("DetectMutualMatch")
            .InDomain("Discovery")
            .InChapter("TheSwiper")
            .TriggeredBy("Dog Liked", TriggerKind.MessageHandler)
            .Command<DetectMutualMatch>()
            .Emits<MutualMatchDetected>();

        model.View<MatchList>()
            .InChapter("TheSwiper")
            .On<DogLiked>()
            .On<DogPassed>()
            .On<MutualMatchDetected>()
            .LinksToSpecification("MatchList/MatchesShow");
    }
}
```

Register it with `services.AddEventModel<K9CrushEventModel>()` and it joins the model on the
**Declared** rung, where a claim the code derives always wins and any difference between the two
shows up as a `SourceDisagreement` hotspot. That gap is the design-first to-do list.

Write your Bobcat specs against the stubs straight away. They are red until the behaviour exists,
and that is the point.

### Nothing regenerates these files

**The segmentation is a set of reported guesses** — which is why it writes files rather than acting
on what it inferred. A wrong guess is a one-line edit in the generated C#, not a re-import, and
since the command never rewrites a file it has already written, your edits cannot be clobbered.

Two details the generated code is careful about, both of which failed loudly the first time:

- **The definition class never shares its namespace's name.** The model name supplies both by
  default, and `namespace K9Crush { class K9Crush }` compiles — then resolves every reference to a
  sibling stub against the *class* first, so `K9Crush.SwipeOnDog` stops meaning what it says. An
  `EventModel` suffix is added only where that collision is real.
- **A generic verb is only used where it names the slice the board named.** `Command<SwipeOnDog>()`
  names the slice after the type, and the slice name is the merge key — so where the board called
  the slice something else, it opens by name and states its pattern and command separately.
  Otherwise one slice would silently become two.

Roles whose type has no stub — a handler, for instance, which is behaviour rather than data — are
declared by **name**: `.HandledBy("SwipeEndpoint")`. That means the same thing to the merge, which
compares a declared type by `Name`, and it keeps the output building.

::: tip The curated `.emodel.yaml` format is retired
This command used to read and validate a curated event-model file, and an import used to write one
for you to review. **YAML is no longer an authoring syntax** (jasperfx#955): the only YAML Bobcat
reads is the Event Modeling platform's own, on import. Hand a curated file to this command and it
says so, and points at `EventModelDefinition`.
:::

### Options

| Flag | What it does |
|---|---|
| `-m, --model <name>` | Model name; defaults to the file name. It is the **merge key**, so it must match what the code-derived sources call the model (`opts.ServiceName` / `[assembly: EventModelName]`) or the import floats off as a second diagram |
| `-n, --namespace <ns>` | Namespace for the generated stubs and definition; defaults to the model name |
| `-o, --out <dir>` | **Directory** the generated C# is written to; defaults beside the input. Created if it does not exist |
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
