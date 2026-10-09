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
event is stored.

### Aggregates: declared, or inferred and called out

A command that does not only start a stream needs a DCB decider or one or more single-stream
aggregates, so the import gives every command one (bobcat#444). A stream the model declares (an
eventmodelers.ai element's `aggregate`) is used as it is. Everything else is **inferred from the
examples**:

- **Lineage groups events into streams.** An example that gives an event and expects another says
  the second is appended where the first was. When an example gives events of several subjects
  (`Home check requested` and `Volunteer approved`), what it expects joins only the givens whose
  name shares a subject with it, so one decision drawing on two streams never folds them into one.
- **A stream is named for its subject**: the longest run of words most of its events open with.
  `AppointmentConfirmed`, `AppointmentCancelled` and `HomeCheckAppointmentProposed` are an
  `Appointment`. A stream no example links to anything joins one whose name its subject ends with
  (`FosterHandoverAppointment` is an `Appointment`), and the report says that was by name only.
- **A slice starts a stream** when it appends to it and no example gives it an earlier event there:
  `.StartsStream<VolunteerApplication>()`. Otherwise it decides against it:
  `.Against<VolunteerApplication>()`.
- **A decision drawing on several streams** decides against each, its command carries an
  `{Aggregate}Id` per stream, and it is flagged: choose several `[WriteAggregate] IEventStream<T>`
  parameters or a DCB decider (bobcat#443).
- **A command left with no aggregate is reported as missing**, with a TODO on the slice. It is
  never quietly made aggregate-less.

Every inferred aggregate is called out, in the report and as a comment on the slice in the
definition, so a wrong guess is a one-line edit there:

```csharp
// ⚠ inferred: decides against Appointment — its examples give Appointment events before it appends; 'Appointment' is the subject 7 of its 7 events share.
model.Command<ConfirmAppointment>()
    .InChapter("BookingAppointments")
    .Against<Appointment>()
    .Emits<AppointmentConfirmed>();
```

Or say it outright on the import, which always wins over the inference. Several at once go after
one flag:

```bash
bobcat import-event-model board.yaml --aggregate ConfirmAppointment=Booking AcceptHomeCheckAssignment=HomeCheck
```

The generated specifications arrange events on the typed aggregate, `GivenEvents<Appointment>(…)`,
and the act addresses it through the command's `Id`, or each `{Aggregate}Id`. A stream with no
aggregate type, `GivenEvents(id, …)`, is left for a stream nothing names at all.

Each chapter gets one `EventModelDefinition`, beside its stubs in `Features/{Chapter}/{Chapter}Model.cs`,
declaring that chapter's slices through the JasperFx.Events fluent API. The chapter is said once at the
top, and so is the aggregate most of the chapter's commands decide against:

```csharp
public class BookingAppointmentsModel : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        model.InChapter("BookingAppointments");
        // ⚠ inferred: 6 of the 6 commands here decide against Appointment, so it is the
        // default; .Against<T>() on a slice replaces it, and .NoAggregate() says it has none.
        model.ForAggregate<Appointment>();

        model.Command<ConfirmAppointment>()
            .TriggeredBy("Confirm Appointment", TriggerKind.Human)
            .Emits<AppointmentConfirmed>()
            .LinksToSpecification("ConfirmAppointment/appointment confirmed");

        model.Command<ProposeHomeCheckAppointment>()
            .StartsStream<Appointment>()   // a slice that starts a stream ignores the default
            .Emits<HomeCheckAppointmentProposed>();
    }
}
```

A chapter whose commands have no clear majority aggregate gets no default, and every command says its
own. The definitions set no `Name`, so every chapter joins the application's model. Pass
`--single-definition` for one `<Model>.cs` holding every slice, each with its own `.InChapter(…)`.

Register each with `services.AddEventModel<BookingAppointmentsModel>()` and it joins the model on the
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
  GlobalUsings.cs                        every namespace the specs use, once
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

An object with more than three specified members is written as a `Property | Value` table rather
than a `.With(...)` chain that runs off the screen. The cells are read with the same rules as any
Bobcat table, and identities are interpolated:

```csharp
await WhenReceived(Specify<BookVisit>($$"""
    | Property | Value        |
    | VisitId  | {{theVisit}} |
    | Vet      | Dr. Hollis   |
    | Room     | 3            |
    | Notes    | EMPTY        |
    """));
```

`Specify<T>(table)` takes either that two-column form or the members as headers over a single row,
and `.With(...)` still chains after it. A value with no faithful cell form, such as text holding a
pipe, keeps the chain instead, one member to a line.

An expected object can also be written as assertions on its members, for checks that are not
plain equality:

```csharp
await ThenReadModel<VolunteerApplicationsQueue>(theQueue, Specify<VolunteerApplicationsQueue>(
    x => x.Pending.ShouldBe(1),
    x => x.Oldest.ShouldBeLessThan(DateTimeOffset.UtcNow)));
```

A plain `ShouldBe` is the same as `.With(...)`. Any other assertion runs against the member's actual
value, and the spec report shows it in the same member table as it reads (`should be less than …`),
with the assertion's own message when it fails. `.Check(...)` adds the same to a `.With(...)` chain.
Checks only verify, so an object with one cannot be built as a command or an arranged event.

The checks are expression trees, and before C# 14 an expression tree cannot leave out an optional argument,
which every Shouldly assertion has (`customMessage`). On net10.0's default language version you write
`x => x.Age.ShouldBe(52)`; on net9.0, or with `LangVersion` pinned below 14, spell the optional arguments
out: `x => x.Age.ShouldBe(52, null)`.

A view example that names no identity is checked as the only one of its type, and the generated spec
says so in a comment, so the assumption is never silent.

Each generated spec class carries `[BobcatSlice(SliceType = typeof(ReviewVolunteerApplication))]`,
or `SliceName = "…"` where no type bears the slice's name, so the IDE navigates from the spec to its
slice. Bobcat.Generators also writes a manifest of every `[BobcatFeature]` test as a JasperFx
`SpecificationBindingDescriptor`: its `{Feature}/{Scenario}` identity, and the command its first
`When…(Specify<T>()…)` or `When…(new T(…))` sends, or the slice `[BobcatSlice]` names. Read it with
`SpecificationBindings.In(assembly)`. JasperFx's `EventModelSpecifications.Link` joins those onto an
assembled Event Model by command type (and domain, for a command several modules handle), so an
`EventModelDefinition` needs no `LinksToSpecification` once something runs that join.

The usings live once in `GlobalUsings.cs`, so a spec file is just its specifications.

`TestSupport.cs` starts the application's own host through Alba, so `Program` must be public
(`public partial class Program;`). Pass `--store marten`, `polecat` or `fisher` to have it run that
store's async daemon in solo mode; without it, the host names each store's way in a comment. The
base spec class calls `ResetAsync()` before every test, which resets every event store the host registers,
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
