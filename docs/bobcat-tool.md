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
Wrote 6 stub type(s) in 3 file(s) under ./Features.
Wrote the event model to ./K9Crush.cs. The segmentation above is a set of guesses, so correct one
with an edit rather than a re-import.
Model 'K9Crush': 3 slice(s), 1 bound specification(s).
```

Pass `--out` the application project's directory; everything lands under it.

### The stubs: one file per slice, a folder per chapter

Each slice gets `Features/{Chapter}/{Slice}.cs`, in the namespace `{Namespace}.{Chapter}`. The file
holds what the slice produces: its command (or the read model of a view), the events it emits and
the messages it publishes. That is also the file `wolverine scaffold` adds the handler to, so a
command and its handler live side by side. A type no slice produces, such as an aggregate or an
event consumed from elsewhere, gets a file of its own under the chapter of the first slice that
names it.

```csharp
// Features/TheSwiper/SwipeOnDog.cs
namespace K9Crush.TheSwiper;

/// <summary>
/// the command of SwipeOnDog.
/// </summary>
public record SwipeOnDog(Guid Id);

/// <summary>
/// emitted by SwipeOnDog.
/// consumed by MatchList.
/// </summary>
public record DogLiked;
```

**A stub has only what the board names, plus an `Id` where it names no identity.** An emlang export
usually carries no field information, since the board's props are intentionally omitted. Events
stay field-less. A command, an aggregate or a read model gets `Guid Id` when the model marks no
identity for it, because that is Wolverine's own convention: a generated specification can address
the stream through it, and a handler's `[WriteAggregate]` resolves it with nothing declared.

**A swimlane is not a stream.** In `Admin / Volunteer approved`, `Admin` is who acts, not where the
event is stored. Only a stream the model declares (an eventmodelers.ai element's `aggregate`)
becomes an aggregate type. Otherwise the events go on a stream with no aggregate type, which
Marten, Polecat and Fisher all support.

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
            .LinksToSpecification("SwipeOnDog/a like is recorded");

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
            .LinksToSpecification("MatchList/matches show");
    }
}
```

Register it with `services.AddEventModel<K9CrushEventModel>()` and it joins the model on the
**Declared** rung, where a claim the code derives always wins and any difference between the two
shows up as a `SourceDisagreement` hotspot. That gap is the design-first to-do list.

Write your Bobcat specs against the stubs straight away. They are red until the behaviour exists,
and that is the point.

With `--specs`, the command writes them for you as well: one WolverineFx.Bobcat specification per
example on the board. They go to the spec project, by default the sibling of `--out` named for the
spec namespace (`--specs-out` to put them elsewhere, `--specs-namespace` to name it; the default is
`{Namespace}.Specs`, and it should be the spec project's name):

```
CritterCrush.Specs/
  TestSupport.cs                         the fixture, the collection, the base spec class
  VolunteeringAndHomeChecks/
    ReviewVolunteerApplication.cs        namespace CritterCrush.Specs.VolunteeringAndHomeChecks
    ...
```

Each slice is one `[BobcatFeature]` class in its own file, under a folder per chapter, and each
example is one `[Fact]` named for the example in snake case:

```csharp
[BobcatFeature("ReviewVolunteerApplication")]
public class review_volunteer_application(AppFixture app) : CritterCrushSpec(app)
{
    [Fact]
    public async Task volunteer_application_reviewed()
    {
        var theStream = Guid.CreateVersion7();
        await GivenEvents(theStream, Specify<VolunteerApplicationSubmitted>());

        await WhenReceived(Specify<ReviewVolunteerApplication>().With(x => x.Id, theStream));

        ThenEvents(Specify<VolunteerApplicationReviewed>());
    }
}
```

Every id a spec mints is `Guid.CreateVersion7()`, never `Guid.NewGuid()`: generated code is copied,
and a random v4 Guid as a stream id fragments the store's indexes.

`TestSupport.cs` holds a placeholder `AppFixture` that only compiles. Replace it with the
application's own host; its comments show the shape for Marten, Polecat and Fisher. The base spec
class calls `ResetAsync()` before every test, which resets every event store the host registers,
so the same specifications run on any of the three stores. They use the application's own database
and schema, so a failing spec's data is where you would look for it.

### The spec project

The spec project is an xUnit v3 executable. It needs **all** of these, or Rider and Visual Studio
show no tests at all ("NuGet package Microsoft.NET.Test.Sdk is not installed"):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.*" />
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.*" />
    <PackageReference Include="Bobcat.Xunit" Version="..." />
    <PackageReference Include="Bobcat.Generators" Version="..." />
    <PackageReference Include="WolverineFx.Bobcat" Version="..." />
    <PackageReference Include="Alba" Version="..." />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\CritterCrush\CritterCrush.csproj" />
  </ItemGroup>
</Project>
```

`Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are the bridge the IDEs discover tests
through; the Microsoft Testing Platform properties are what `dotnet test` and running the
executable use. Bobcat.Generators warns with **BOBCAT033** when a project references `xunit.v3`
without `Microsoft.NET.Test.Sdk`.

The links in the definition are the identities those specs report: the slice name, then the
method name read back as a sentence. That's why the example `ALikeIsRecorded` is linked as
`SwipeOnDog/a like is recorded`. A projected test's scenario title *is* its method name, so linking
the board's own spelling would bind to a scenario no run ever reports, and every slice would stay
unproven with all of its specs green. An example that the board attaches to no slice still gets a
spec, under a feature named for its chapter, and the report flags that it binds to nothing.

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

The tool is the front door to the Event Modeling workflow: get a model in as C#, review the
segmentation, then write specs against the stubs and build slice by slice. Skeleton generation for
declared-only slices is Wolverine's `scaffold` command (JasperFx/wolverine#4832), not this tool —
Bobcat's own scaffolder was retired with issue #406. See
[Event Modeling and Spec Driven Development](tutorials/event-modeling.md) for the whole path, and
[Spec Identities](spec-identities.md) for the gate that keeps the model and the code honest.
