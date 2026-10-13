# The `bobcat` Tool

A global .NET tool with these commands:

| | |
|---|---|
| **[`run`](#run-preview-pick-watch)** | build a spec project, run it, and show its specifications rather than its log. The default command |
| **[`preview`](#run-preview-pick-watch)** | show a spec project's specifications without running them |
| **[`pick`](#run-preview-pick-watch)** | choose specifications from a menu, then run them |
| **[`watch`](#run-preview-pick-watch)** | run a spec project, and again whenever its source changes |
| **[`import-event-model`](#import-event-model)** | convert an eventmodelers.ai board export into **C#**: stub records, an `EventModelDefinition` per chapter, and optionally a specification per example plus the spec project around them |
| **[`resident`](#resident)** | keep a suite whose process Bobcat does not own available to a run console, running the specifications the console asks for |
| **[`ui`](bobcat-ui.md)** | serve the [Bobcat UI](bobcat-ui.md) -- the live run board and the Event Model canvas -- on 5525 and open it in the browser, until Ctrl+C |
| **[`watch-event-model`](bobcat-ui.md#getting-a-model-onto-it)** | keep an application's Event Model live in the Bobcat UI as you edit it, starting a UI if none is running |
| **[`list-runs`](agent-queries.md)**, **`run-status`**, **`failing-tests`**, **`failing-spec`**, **`flaky-ledger`**, **`await-run`**, **`export-run`** | read the runs a running Bobcat UI holds, as JSON -- see [Querying Runs from the Command Line](agent-queries.md) |
| **[`event-model`](agent-queries.md)**, **`slice-coverage`** | read the Event Model a running Bobcat UI holds, and what of it is untested, as JSON |

`dotnet test`, or running the test executable, still runs a spec project; the run commands are a
friendlier view of the same test host.

```bash
dotnet tool install -g Bobcat.Console
```

`Bobcat.Console` carries the whole free toolset. Most of its commands start, do their job and exit;
`ui` is the one that stays up, serving the [Bobcat UI](bobcat-ui.md) until you stop it.

::: tip The UI came back
The run board and the Event Model canvas lived in [Stoat](https://github.com/JasperFx/stoat) from
2026-09-18 and returned to Bobcat as `bobcat ui` in October 2026
([#524](https://github.com/JasperFx/bobcat/issues/524)). Runs publish to it exactly as they always
have; see [What a Run Publishes](monitor-design.md).
:::

## `run`, `preview`, `pick`, `watch` {#run-preview-pick-watch}

```bash
bobcat                                  # run the spec project found from the current directory
bobcat run src/CritterCrush.Specs --feature Booking
bobcat preview
bobcat pick
bobcat watch --scenario "home check"
```

Each takes the spec project (a `.csproj`, or a directory holding one; found from the current
directory when omitted), builds it, and drives its test host. The host's own output goes to a file
and the terminal gets the Feature / scenario / step tree. `watch` reruns on every change to the spec
project or anything it references: Enter reruns, `p` picks, `a` goes back to everything, `q` quits.

| Flag | What it does |
|---|---|
| `-f, --framework <tfm>` | the target framework to run; the newest the project targets by default |
| `-c, --configuration <name>` | the build configuration; `Debug` by default |
| `--feature <text>` | only features whose title contains this, case-insensitive; repeatable (not `pick`) |
| `--scenario <text>` | only scenarios whose title contains this, case-insensitive; repeatable (not `pick`) |
| `--no-build` | use the last build as it is |

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

**A command that starts a stream gets no `Id`** (bobcat#453). Its handler mints the new stream's id,
a sequential Guid (`Guid.CreateVersion7()`), so an `Id` on the command would be a member nothing
reads. An example that gives the command an id keeps it. A stream keyed by a **string**, a natural
key such as an email, is the exception: nothing can mint one, so the command carries `string Id` and
the new stream takes it. A generated specification for a stream start reads the new stream's id back
from the act, rather than declaring one the handler could never see:

```csharp
await WhenReceived(Specify<SubmitApplication>().With(x => x.Areas, "dogs"));

var theApplication = TheStartedStream<Application>();
await ThenStreamIsStartedWithEvents<Application>(Specify<ApplicationSubmitted>()
    .With(x => x.Areas, "dogs")
    .With(x => x.ApplicationId, theApplication));
```

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

`--starts-stream Slice=Type` says that a slice **starts** a stream (bobcat#454). The slice's
`--aggregate` overrides are then the aggregates it decides against besides, and what its examples
give first is never joined to the stream it starts.

**Import part of a board with `--chapter`.** `--chapter BookingAppointments VolunteeringAndHomeChecks`
imports only those chapters. The rest of the board is still read, as evidence. When the only events
an example gives before a command acts come from a chapter you left out, nothing imported shows the
stream existing first, so the command may well start it. The import asks:

```
⚠ slice 'RequestHomeCheck' starts the HomeCheck stream? It is inferred to decide against HomeCheck,
but the only HomeCheck events its examples give first, ApplicationReviewed, come from chapter
'ShelterReviewsApplication', which was not imported. If it starts the stream, import with
--starts-stream RequestHomeCheck=HomeCheck.
```

The same question is asked, without a chapter name, when no slice on the board emits the event.

**`--json` for tooling** (bobcat#454). With `--json`, the files are written as usual but stdout carries
nothing but JSON: every callout, the text report, and every file, marked `write`, `merge` or `kept`. Each
callout has a stable `code`, the aggregates it is about, the evidence it was drawn from, and the flag
that settles it:

```json
{
  "slice": "RequestHomeCheck",
  "code": "stream-start-from-unimported-chapter",
  "aggregates": [ "HomeCheck" ],
  "message": "⚠ starts the HomeCheck stream? …",
  "evidence": {
    "events": [ "ApplicationReviewed" ],
    "examples": [ "home check requested" ],
    "chapters": [ "ShelterReviewsApplication" ]
  },
  "override": "--starts-stream RequestHomeCheck=HomeCheck"
}
```

| Code | Means | Settled by |
|---|---|---|
| `inferred-stream-start` | No example gives the slice an earlier event on the stream, so it starts it | `--starts-stream Slice=Type` |
| `inferred-against` | Its examples give the aggregate's events first, so it decides against it | `--aggregate Slice=Type` |
| `stream-start-from-unimported-chapter` | Decides against an aggregate only on evidence nothing imported emits; it may start the stream | `--starts-stream Slice=Type` |
| `stream-start-unknown` | No example says whether it starts the stream, so it is assumed to append | `--starts-stream Slice=Type` |
| `several-streams` | Draws on several streams: several `[WriteAggregate]` streams or a DCB decider | a design decision, no flag |
| `missing-aggregate` | No aggregate could be declared or inferred | `--aggregate Slice=Type` |
| `strong-typed-id-name-taken` | `--strong-typed-ids` was asked for and the board already names a type `{Aggregate}Id`, so that aggregate keeps a primitive identity | rename the board's element |
| `spec-project-file-kept` | A spec project file (`.csproj`, `AssemblyAttributes.cs`) already exists and was kept | nothing; it is yours |
| `spec-project-missing-reference` | The existing spec project lacks a package the specs need | the `fix`: a `PackageReference` |
| `spec-project-missing-event-model-name` | The existing spec project declares no `[assembly: EventModelName]` | the `fix`: the attribute |
| `spec-project-not-in-solution` | The spec project is not in the solution beside it | the `fix`: `dotnet sln … add …` |
| `global-json-missing-test-runner` | The governing `global.json` does not select Microsoft.Testing.Platform | the `fix`: its `test` section |
| `central-package-versions` | Versions are managed centrally | the `fix`: the `<PackageVersion>` lines |
| `app-program-not-public`, `app-missing-run-jasperfx-commands`, `app-missing-runtime-compilation`, `app-project-not-found` | What the application lacks (see [The spec project](#the-spec-project)) | the `fix` |
| `merged-slice-declaration` | A re-import declared a new slice at the end of its chapter's definition | nothing; review it |
| `merged-aggregate-declaration` | A slice merged in names an aggregate no definition declared, so it was declared beside it | nothing; review it |
| `merged-type` | A re-import added a type the board names to an existing stub file | nothing; review it |
| `merged-spec` | A re-import added a spec for a new example to the existing class bound to its feature | nothing; it is red until the behaviour exists |
| `merged-using` | A re-import added a `global using` to the spec project's `GlobalUsings.cs` | nothing |
| `type-differs-from-board` | A type lacks a member the board names, or types it differently. The type was not touched | edit the type, if the board is right |
| `merged-slice-roles` | The board gives an existing slice roles (`Emits`, `Publishes`, `On`, `Reads`, `Produces`, `Projects`) no declaration of it says, so a statement reopening the slice by name was added at the end of the definition's `Configure`. `roles` lists them | nothing; review it |
| `slice-differs-from-board` | The board gives an existing slice roles no declaration says, and they could not be added: the slice is declared in a file other than its chapter's definition | the `fix`: the calls to add, and where |
| `slice-aggregate-differs` | The board's aggregate for an existing slice (the stream it starts, what it decides against) isn't what its declaration says. Nothing was changed | the `fix`, if the board is right; the `override` flags, when the board's aggregate is inferred and the code is right |
| `slice-trigger-differs` | The board's trigger for an existing slice (its label or kind) isn't what its declaration says. Nothing was changed | the `fix`, if the board is right |
| `slice-pattern-differs` | The board's pattern for an existing slice isn't what its declaration says. Nothing was changed | the `fix`, if the board is right |
| `slice-role-removed-on-board` | A declaration says a role the board doesn't give the slice. Nothing was removed | the `fix`, if the board is right; nothing, if you added the role by hand |
| `slice-removed-on-board` | A definition declares a slice the board doesn't have. Nothing was removed | the `fix`, if the board is right; nothing, if you declared the slice by hand |
| `merge-skipped-unparseable` | An existing file does not parse, so nothing was merged into it | fix the file and import again |
| `merge-skipped-no-insertion-point` | The namespace, definition class, `Configure` method or feature class to insert into is not in the file | add it by hand |
| `merge-skipped-ambiguous` | More than one place could take the addition, so none was chosen | add it by hand |

Callouts about the project rather than a slice have an empty `slice`, a `file` naming what they are
about, and a `fix` holding the exact line, package or command. The merge callouts carry a `file`, a
`type` when they are about one type, `roles` (each as `Emits(AppointmentNoShowRecorded)`) when they are
about a slice's roles, and a `fix` when there is an edit to make by hand.

The messages may be reworded; a code never is, and codes are only ever added (the same contract as
`wolverine scaffold --json`).

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

#### `--strong-typed-ids`: an identity type per aggregate

Without it an identity the model does not type is a `Guid`, which is what every Critter Stack store
defaults a stream id to. With it each aggregate gets one, in `Identities.cs` in the root namespace:

```csharp
[JsonConverter(typeof(OrderId.Converter))]
public readonly record struct OrderId(Guid Value) : IParsable<OrderId>
{
    public override string ToString() => Value.ToString();
    public static OrderId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));
    // TryParse, and a nested JSON converter so the wire carries the value
}
```

Everything that addresses that stream then takes it — the aggregate's `Id`, the command's `Id` or
`{Aggregate}Id`, the identity member of each of its events (including one the board marks, such as an
`Email` keying a customer), the read model projected from it, and the generated specifications:

```csharp
var theOrder = new OrderId(Guid.CreateVersion7());

await GivenEvents<Order>(theOrder, Specify<OrderPlaced>().With(x => x.OrderId, theOrder));
```

and the definition declares it, which is a **Declared** claim the store's own projection later agrees
with (jasperfx#1004):

```csharp
model.Aggregate<Order>().IdentifiedBy<OrderId>();
```

Three things worth knowing:

- **The name is the requirement.** Marten treats a type as a strong-typed identity only when its name
  ends in `Id` (case sensitive), so `{Aggregate}Id` is not a style choice. The struct wraps what the
  stream's id would otherwise have been — a `Guid`, or a `string` for a natural key.
- **A read model projected from the stream is keyed by the same type.** A single-stream projection's
  document id must *be* the stream's identity type, so `OrderSummary(OrderId Id)` is correctness, not
  tidiness.
- **An aggregate whose `{Aggregate}Id` the board already uses as an element keeps a primitive id**, and
  the import says so (`strong-typed-id-name-taken`): two types of one name in one namespace is output
  that does not build.

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

**A member check needs C# 14 to read naturally**, because it is an expression tree and an expression
tree cannot leave out an optional argument before then -- see [Expected
Objects](expected-objects.md#member-checks-and-the-c-version-they-need) for the error, the fix, and
every other form an expected object takes.

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
(`public partial class Program;`; the import checks, see below). Pass `--store marten`, `polecat` or `fisher` to have it run that
store's async daemon in solo mode; without it, the host names each store's way in a comment. The
base spec class calls `ResetAsync()` before every test, which resets every event store the host registers,
so the same specifications run on any of the three stores. They use the application's own database
and schema, so a failing spec's data is where you would look for it.

### The spec project

When the spec directory holds no `.csproj`, `--specs` writes the project around the specifications
too, so the result builds and runs with `dotnet test` and nothing written by hand:

| File | What it holds |
|---|---|
| `{SpecsNamespace}.csproj` | an xUnit v3 (4.x) executable on Microsoft.Testing.Platform 2: `xunit.v3`, `Bobcat`, `Bobcat.Xunit`, `Bobcat.Generators` (as an analyzer, with the `Bobcat.Generated` interceptor opt-in, BOBCAT037), `WolverineFx.Bobcat`, `Alba`, `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` for IDE discovery, and a `ProjectReference` to the application. Its target framework is the application's (the newest, if it has several) |
| `AssemblyAttributes.cs` | `[assembly: EventModelName("{Model}")]`, so the specs' slices join the application's model; `Parallelization(Mode = None)`, because every spec resets the store; and `WebApplicationFactoryContentRoot` pointing Alba at the application's directory |
| `global.json` | `"test": { "runner": "Microsoft.Testing.Platform" }`, which `dotnet test` needs for xUnit v3 4.x on the .NET 10 SDK |

**Versions.** The Bobcat packages are pinned to the tool's own version (`--bobcat-version` changes
it). `WolverineFx.Bobcat` defaults to the newest one published; pass `--wolverine-version` for
another, such as a local pack. Both are MSBuild properties in the csproj, `BobcatVersion` and
`WolverineFxBobcatVersion`, so `-p:WolverineFxBobcatVersion=…` or a `Directory.Build.props`
overrides them later. When the repository manages versions centrally (`Directory.Packages.props`),
the references carry no versions and a `central-package-versions` callout lists the
`<PackageVersion>` lines to add.

**Where `global.json` goes.** The SDK reads the `global.json` nearest the directory `dotnet test`
runs in. If one already exists anywhere above the spec project, it is never shadowed. It is checked
instead, and a `global-json-missing-test-runner` callout gives the section to add. If there is none,
one is written at the solution root (the nearest directory above holding a `.sln` or `.slnx`, else
the git root), so `dotnet test` works from the root and from the spec project alike. With neither,
it goes beside the spec project.

**Nothing here is ever overwritten, not even with `--force`.** An existing `.csproj` means the
project is yours: nothing is written into it, and it is checked instead. A missing package
reference, or no `[assembly: EventModelName]`, is a callout with the line to add.

**The application is checked, never edited.** The application project is the one `.csproj` in
`--out` (`--app-project` names it otherwise). Each thing the specs need from it is a callout carrying
the exact fix:

| Code | Missing | Fix |
|---|---|---|
| `app-program-not-public` | a public `Program`, which Alba needs | `public partial class Program;` at the end of `Program.cs` |
| `app-missing-run-jasperfx-commands` | `RunJasperFxCommands`, which `wolverine scaffold` and codegen run through | `return await app.RunJasperFxCommands(args);` in place of `app.Run();` |
| `app-missing-runtime-compilation` | `WolverineFx.RuntimeCompilation`; without it a `TypeLoadMode.Dynamic` host fails to start and every spec fails | `dotnet add <app.csproj> package WolverineFx.RuntimeCompilation --version <the app's WolverineFx version>` |
| `app-project-not-found` | no single `.csproj` in `--out`; the spec project gets a TODO in place of the reference | `--app-project <path>` |

<!-- JEREMY -->

The links in the definition are the identities those specs report: the slice name, then the
method name read back as a sentence. That's why the example `ALikeIsRecorded` is linked as
`SwipeOnDog/a like is recorded`. A projected test's scenario title *is* its method name, so linking
the board's own spelling would bind to a scenario no run ever reports, and every slice would stay
unproven with all of its specs green. An example that the board attaches to no slice still gets a
spec, under a feature named for its chapter, and the report flags that it binds to nothing.

### Nothing regenerates these files

**The segmentation is a set of reported guesses** -- which is why it writes files rather than acting
on what it inferred. A wrong guess is a one-line edit in the generated C#, not a re-import, and
since the command never rewrites a file it has already written, your edits cannot be clobbered.

### Re-running the import

Without `--force`, a re-import is **additive**. A file that doesn't exist is written. A file that
does exist gets what the board has and the file lacks, and nothing in it is changed or removed:

| File | What is added | What is never touched |
|---|---|---|
| The chapter's `EventModelDefinition` | a declaration for each slice no definition in `--out` declares, at the end of `Configure`, written to agree with the file's own `InChapter` / `InDomain` / `ForAggregate`; a `model.Aggregate<T>()` for an aggregate such a slice names that nothing declares; a statement reopening an existing slice by name with the roles the board now gives it (see below) | every existing declaration, even where the board now says something else (the `slice-*` callouts say what) |
| A slice's stub file (`Features/{Chapter}/{Slice}.cs`) | each type the board names that no file in `--out` declares, at the end of the file's namespace, plus any `using` it needs | every existing type, even where its members differ from the board's (`type-differs-from-board`) |
| A slice's specification file | a spec for each example that has none, at the end of the class bound to its `[BobcatFeature]`, wherever that class now lives | every existing spec method |
| `GlobalUsings.cs` | the namespaces it lacks | the rest |

- **The whole directory is checked, not just the file.** A type, slice or spec class you moved to
  another file is found there and never declared twice. A type a new stub file would declare is
  left out of it when another file already declares it.
- **Roslyn finds the insertion point; the edit is a text insertion.** Every byte you wrote stays
  where it was, whatever your formatting. A file that doesn't parse, a missing class or namespace,
  or more than one candidate is skipped with a `merge-skipped-*` callout. Nothing is forced.
- **A type is compared on the members the board names.** Members you added are not a difference.
  A board member missing from the type, or typed differently, is.
- **It is idempotent.** Run it twice and the second run changes nothing.
- Each merge prints a line, e.g. `Added 1 slice declaration (SubmitHomeCheckReport) to
  Features/VolunteeringAndHomeChecks/VolunteeringAndHomeChecksModel.cs.` or `Added 1 role
  (RecordAppointmentNoShow: Emits(AppointmentNoShowRecorded)) to
  Features/BookingAppointments/BookingAppointmentsModel.cs.`, and `--json` lists the file with
  `"mode": "merge"`.
- **`--force` still overwrites**, as before. The spec project's `.csproj`, `global.json` and
  `AssemblyAttributes.cs` are never touched, with or without it, and `TestSupport.cs` is written only
  when it is missing.

#### A new role on an existing slice

When the board gives a slice the definition already declares a new event, message, consumed event,
read, read model or projection, the stub file gains the type and the definition gains a statement at
the end of `Configure` that **reopens the slice by name** with just the missing roles:

```csharp
        model.Command<RecordAppointmentNoShow>()
            .TriggeredBy("Record Appointment No-Show", TriggerKind.Human);

        model.Slice("RecordAppointmentNoShow").Emits<AppointmentNoShowRecorded>();
```

The declaration itself is never edited. JasperFx folds every statement naming a slice into one slice
and unions their roles, in one definition or across several, so the model and `wolverine scaffold` see
`RecordAppointmentNoShow` emitting both events, and `scaffold` writes the `Apply` for the new one.

- **Several new roles for one slice are one statement**, a call per line.
- **A role is already there if any statement says it**, the declaration, an earlier reopening statement,
  or one you moved to another definition, so a re-run adds nothing. Fold it into the declaration and
  delete the reopening statement if you prefer; the next import won't add it back.
- **A declaration that names its own chapter or domain** (`.InChapter("Elsewhere")`) is reopened in that
  chapter, or the two statements would disagree.
- `merged-slice-roles` says what was added. A slice declared in another file than its chapter's
  definition is not reopened; `slice-differs-from-board` gives the calls to add there.

#### What a re-import reports and never changes

Comparing the board's declaration with the file's, declared to declared, a re-import reports, and never
applies:

| Code | When |
|---|---|
| `slice-aggregate-differs` | the board's aggregate for a slice isn't the declaration's. A board that claims no aggregate is no disagreement, and neither is a declared DCB decider |
| `slice-trigger-differs` | the board states a trigger label or kind the declaration doesn't say. A trigger you added where the board states none is not reported |
| `slice-pattern-differs` | the board's pattern isn't the declaration's |
| `slice-role-removed-on-board` | a declaration says a role the board doesn't give the slice |
| `slice-removed-on-board` | a definition declares a slice the board doesn't have, including every slice of a chapter the board dropped (any definition in `--out` carrying the import's header comment is compared). With `--chapter`, the whole board counts, so a slice the board moved to a chapter left out isn't reported |

Each one's `fix` is the edit to make by hand, e.g. `Delete .Emits<ReminderScheduled>() from the
declaration of slice ConfirmAppointment in BookingModel.cs.` The board can't tell a removal from
something you added yourself, so a role or slice you declared by hand is reported too; leave it.

<!-- JEREMY: why additive -- the board keeps moving after the first import, and the files are where the
corrections live. -->

Two details the generated code is careful about, both of which failed loudly the first time:

- **The definition class never shares its namespace's name.** The model name supplies both by
  default, and `namespace K9Crush { class K9Crush }` compiles -- then resolves every reference to a
  sibling stub against the *class* first, so `K9Crush.SwipeOnDog` stops meaning what it says. An
  `EventModel` suffix is added only where that collision is real.
- **A generic verb is only used where it names the slice the board named.** `Command<SwipeOnDog>()`
  names the slice after the type, and the slice name is the merge key -- so where the board called
  the slice something else, it opens by name and states its pattern and command separately.
  Otherwise one slice would silently become two.

Roles whose type has no stub -- a handler, for instance, which is behaviour rather than data -- are
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
| `-o, --out <dir>` | **Directory** the generated C# is written to, normally the application project's; defaults beside the input. Created if it does not exist |
| `--specs` | also write a WolverineFx.Bobcat specification per example, and the spec project when there is none |
| `--specs-out <dir>` | where the specifications go; defaults to a sibling of `--out` named for the spec namespace |
| `--specs-namespace <ns>` | root namespace of the specifications, and the spec project's name; defaults to `{Namespace}.Specs` |
| `--store <marten\|polecat\|fisher>` | how the generated test host runs the store's async daemon |
| `--wolverine-version <v>` | `WolverineFx.Bobcat` version for a spec project the import writes; defaults to the newest published |
| `--bobcat-version <v>` | Bobcat package version for a spec project the import writes; defaults to the tool's own |
| `--app-project <csproj>` | the application project the spec project references and the checks read; defaults to the one `.csproj` in `--out` |
| `--single-definition` | one `EventModelDefinition` for the whole model instead of one per chapter |
| `--strong-typed-ids` | give each aggregate an identity type — `OrderId` in `Identities.cs` — and write every member, specification and `IdentifiedBy` that addresses its stream with it |
| `--aggregate Slice=Type …` | the aggregate a slice decides against, over the inference |
| `--starts-stream Slice=Type …` | the aggregate whose stream a slice starts, over the inference |
| `--chapter A B …` | import only these chapters; the rest of the board is still read as evidence |
| `--json` | stdout carries only JSON: callouts, report and files |
| `--force` | overwrite generated stubs, definitions and specifications that exist, instead of [merging into them](#re-running-the-import). Never the spec project's `.csproj`, `global.json` or `AssemblyAttributes.cs` |
| `-u, --url <base>` | Push the assembled model to a [Bobcat UI](bobcat-ui.md) at this base URL |

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

A [resident runner](resident-runner.md) for a spec suite, whose `Main` belongs to its test framework.
It asks the host what it specifies, registers those identities with the console, and runs a command
by driving that host narrowed to the specifications the command named -- a fresh process per
command (`cold`), or one host held open between commands (`warm`), as the console chooses.

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

**It ships here because the console at the other end references nothing else in this repository**, and so
cannot be handed a class to host. A wiring mistake is refused at launch rather than at the first
button press -- a host that is not built, a suite that lists no specifications (usually a spec project
with no runner adapter referenced), or a framework whose filter spelling Bobcat will not guess at.
A runner that registered and then refused every command would look broken rather than unsupported.

It exits **75** after a `restart` command and **0** on an orderly stop, so a parent can tell
"relaunch me" from "I'm done".

## Commands

```bash
bobcat help                        # list the commands
bobcat help import-event-model     # usage for one
```

The tool registers its commands explicitly rather than scanning, so nothing an assembly happens to
carry can join this surface. A bare `bobcat` is `bobcat run`: it runs the spec project found from the
current directory, and fails fast when there is none.

## Where this fits

The tool is the front door to the Event Modeling workflow: get a model in as C#, review the
segmentation, then write specs against the stubs and build slice by slice. Skeleton generation for
declared-only slices is Wolverine's `scaffold` command (JasperFx/wolverine#4832), not this tool  -- 
Bobcat's own scaffolder was retired with issue #406. See
[Event Modeling and Spec Driven Development](tutorials/event-modeling.md) for the whole path, and
[Spec Identities](spec-identities.md) for the gate that keeps the model and the code honest.
