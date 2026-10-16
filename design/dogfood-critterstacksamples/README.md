# Dogfood: every CritterStackSamples test project on Bobcat (bobcat#559)

2026-10-16. Every test project in `JasperFx/CritterStackSamples` (at 89ea1d9) was converted to a Bobcat spec project
**the way a package consumer would do it**, with the packages from nuget.org (Bobcat / Bobcat.Xunit / Bobcat.Generators
**0.60.4**, WolverineFx.Bobcat(.Http) **6.51.0-alpha.1**, xunit.v3 4.0.2 on MTP 2). The only guide was the
`critterstack-sdd-bobcat-authoring` skill. The ported code was scratch, in a throwaway clone, and was not kept. The
findings are the deliverable: one file per project beside this one, and the method in `method.md`.

The in-repo `samples/` are store-only ports that reference Bobcat by `ProjectReference`, so they never exercise WolverineFx.Bobcat
or the package path. That gap is what this run covered.

## Results

| Project | Tests before | Specs after | Result |
|---|---|---|---|
| BankAccountES | 10 | 10 | 10 pass |
| BookingMonolith | 16 | 16 | 16 pass |
| CleanArchitectureTodos | 11 | 11 | 11 pass |
| ContributorApi | 11 | 11 | 11 pass |
| CqrsMinimalApi | 6 | 5 specs + 1 plain `[Fact]` | 6 pass |
| EcommerceMicroservices (Catalog only; the other services have no tests) | 14 | 14 | 14 pass |
| EcommerceModularMonolith | 17 | 20 | 20 pass |
| LibraryQuickstart | 4 | 6 | 6 pass |
| MeetingGroupMonolith | 10 | 13 | 13 pass |
| MoreSpeakers | 10 | 10 | 10 pass |
| OutboxDemo | 4 | 4 | 4 pass |
| PaymentsMonolith | 10 | 11 | 11 pass |
| ProjectManagement | 1 | 2 | 2 fail, both informative: a WolverineFx.Bobcat gap, and a **real bug in the sample** (`new AdminAssigned(command.Name)` where `command.Admin` was meant) that the original test never caught |
| ShipmentTracking | 33 | 33 | build only (needs SQL Server + RabbitMQ) |
| critterwatch/Embedded.Sqlite | 6 | 7 | 7 pass |
| critterwatch/Fleet.PostgresqlQueues | 2 | 2 | build + listing only (Aspire starts containers) |
| critterwatch/Fleet.{RabbitMq,AzureServiceBus,GooglePubSub,SqlServerQueues}, WebService.Http | 12 | — | assessed, not converted (same shape as Fleet.PostgresqlQueues) |

That is **139 specifications green** across 13 projects. No crash came from Bobcat. Apart from BOBCAT027 on a generic step,
no BOBCAT diagnostic fired anywhere. The interceptor opt-in from the package's buildTransitive props worked every time, and
BOBCAT037 never appeared. The specs are usually *stronger* than the tests they replaced: arranging by events or documents
instead of POST chains removed every `Task.Delay`, cascades and integration messages are now asserted, and the run exposed
ProjectManagement's bug. The spec manifest (`BOBCAT_LIST_SPECS`) was right in **every** project, with the expected
`{Feature}/{Scenario}` identity for each spec.

## Trends

Each trend is listed with the projects that hit it. The Bobcat issues are filed. The WolverineFx.Bobcat and ai-skills items
are proposed below and not yet filed.

### 1. The Event Model comes out wrong in four predictable ways

The manifest is always right. The slice model is right for single-object reads, interpolated-route acts and
event-sourced command slices, and wrong in four repeatable places:

- **A list read is not a read act** (11 projects): `WhenReading<List<T>>`, `T[]` and `IReadOnlyList<T>` make the slice a View, or
  leave it with no pattern, and drop the GET trigger and route. An explicit `Pattern = "Query"` doesn't rescue it. Filed as **#612**.
- **A by-type HTTP act loses its route** (12 projects): `WhenPosted(cmd)` / `WhenPut(cmd)`, the recommended form, stamp no route or
  verb. **#613**.
- **Documents arranged for a command become its read models** (5 projects): the `[Entity]` lookups show up as views the command
  produces. **#614**.
- **An act with no command role is drawn wrong** (7 projects): a delete becomes a View, unrelated specs merge into a slice named
  after the feature (LibraryQuickstart invented a "Library" view), or the spec silently leaves the model. **#615**.

Two more are decisions rather than bugs, so they are not filed:
- The messages a *downstream* handler published (OutboxDemo's saga) are stamped on the act's slice, because the spec asserted them.
- A saga arranged with `GivenReadModel` is stamped as a read model. A saga is closer to an aggregate.

### 2. Half the samples are document-only, and the vocabulary and the skill are event-shaped

CqrsMinimalApi, CleanArchitectureTodos, ContributorApi, EcommerceMicroservices, EcommerceModularMonolith, MeetingGroupMonolith,
MoreSpeakers, OutboxDemo, PaymentsMonolith and ShipmentTracking use Marten or Polecat **documents**, not streams. The
vocabulary mostly coped (`GivenReadModel`, `ThenDocument`, `ThenNoReadModel`, `ThenMessageSent`). The gaps:
- No step for a document whose id the system chose (4 projects wrote the same raw `QuerySession` helper). `ThenSingleDocument`
  is stream-bound and refuses. **#616**.
- A "read model has caught up" step before every document read, even with no projections. **#620**.
- Documents are called "read models" everywhere, there are no saga steps, and arranged documents land on the model as read models (#614).
- The skill has no document-app section at all.

### 3. The HTTP acts WolverineFx.Bobcat is missing (proposed, not filed: wolverine repo)

These are the most common reasons a spec fell back to raw Alba:
- **Body-less acts**: a POST or DELETE taking only route values (`/books/{id}/return`, `/proposals/{id}/accept`, `DELETE /x/{id}`).
  Hit in 7 projects. `new { }` works for most endpoints, renders `<>f__AnonymousType0` and costs the slice (#615). A `[FromQuery]`
  DELETE answers **404** to the JSON body (CqrsMinimalApi).
- **`WhenPut<TResponse>` / `WhenPatched<TResponse>` / `WhenDeleted<TResponse>`**, for parity with `WhenPosted<TResponse>` (4 projects).
- **Route values on `WhenPosted/WhenPut(cmd, route)`**, like `WhenReading(route, values)`. Today a `{param}` template is posted
  literally (404).
- **An HTTP act against a non-Wolverine endpoint sees no events** (ProjectManagement, a Minimal API `MapPost`):
  `WolverineHttpScenario.sendAsync` doesn't do the GH-4977 high-water read-back that `runActAsync` does, so `ThenNoEvents()` is
  a **false green**. High severity.
- A non-JSON read (`WhenReading<T>` throws `JsonException` on an HTML page: Embedded.Sqlite), and a response-header step (`Location`).
- Wolverine-side steps ShipmentTracking needed: executed messages, published-with-no-route, "the act succeeded", playing
  scheduled messages as an act, and a `GivenReceived(message)` arrange that renders as Given.
- `WhenPosted`'s `[EventModelTrigger]` has no `HttpMethod = "POST"`.
- WolverineFx.Bobcat.Http **depends on Alba 9.0.0-rc.2**. A consumer pinning Alba 8.x gets NU1605, and Alba 9 removes
  `ConfigurationOverride` (ShipmentTracking).
- The envelope and HTTP-exchange report tables need column widths (cells wrap at about 10 characters) and a cap on the response
  column (Embedded.Sqlite printed a whole HTML page).

### 4. The published package lags the docs

The skill and CLAUDE.md teach `Expect.Exactly / InAnyOrder / Containing / NoneOf / Rows`, `Within`, `InOrder` (#456) and the
results file (#540). **None of them are in 0.60.4.** Four projects (BankAccountES, CqrsMinimalApi, MeetingGroupMonolith, EcommerceModularMonolith) hit `CS0117: 'Expect' does not contain a definition for 'Exactly'`,
and `BOBCAT_RESULTS` silently wrote nothing. The fix is the release (#568). Until then the skill should say "after 0.60.4".
The collection-member rendering bug (`System.Collections.Generic.List`1[System.String]` in a grid cell) is fixed on main by the
same #456 work (28be13b).

### 5. What a rendered spec looks like

- An **empty first row** under every step with a table and a step-level cell: long commands and ProblemDetails refusals (7 projects). **#617**.
- **CLR names and aliases** where a reader wants values: ``List`1``, `<>f__AnonymousType0`, `there is no Product Product`,
  `ClientId: Client`, and `GivenReadModel` showing no values for a class (every project). **#618**.

### 6. Running specs as an agent

- **Passing specs print nothing** when output is not a TTY, and under `dotnet test` even with `BOBCAT_SPEC_CONSOLE=1`.
  You have to run the test executable with `BOBCAT_SPEC_CONSOLE=1`. That is by design (a captured stream belongs to MTP),
  but it should be the first line of the skill's "running" section.
- **A plain `dotnet test` publishes to whatever console holds :5525.** On this shared machine that was another session's `bobcat ui`,
  and the refusal message told us to rename our model to theirs. **#621**.
- `BOBCAT_SPEC_PREVIEW` shows none of the vocabulary steps (4 of 33 specs, and only the suite's own helpers). **#625**.
- No XML docs in the Bobcat package, so no IntelliSense for `CritterStackSpec`. **#622**.
- Mixed sync and async Then steps: a forgotten `await` is only CS4014 and the assertion runs unobserved. **#623**.
- A member check written as a chained Shouldly call errors at run time instead of failing at compile time. **#624**.
- Two overload papercuts every document spec hits: `GivenReadModel<T>(object)` inference and an ambiguous `With(x => x.P, null)`. **#619**.
- A `SpecAssert` in the test body (no step open) is silent on pass, so a reader thinks the assertion vanished. This is documented
  in CLAUDE.md, not in the skill.

Already fixed on main since 0.60.4: a non-public step helper is BOBCAT039 instead of CS0122 in generated code (#535), and
generic steps are intercepted with their type arguments (#534; MeetingGroupMonolith's BOBCAT027 on `{T}`).

### 7. Adoption cost: the recipe is identical everywhere, and xUnit v3 is the real price

After the first project, every conversion took about five minutes of copying, and the list never changed:
- WolverineFx.* to 6.51.0, plus Marten (and companion packages such as `Marten.AspNetCore`) in lockstep. Apps could stay on net9.0.
- The test project goes from xUnit v2 / VSTest to xunit.v3 + MTP: `OutputType=Exe`, the two MTP properties, and `global.json`
  `{ "test": { "runner": "Microsoft.Testing.Platform" } }`. `IAsyncLifetime` moves from `Task` to `ValueTask`. A shared harness
  library (critterwatch's) has to move too, before any solution consuming it can adopt Bobcat.
- **Re-add `<Using Include="Xunit" />`** (xunit.v3 has no implicit using), and **keep the project's `AssemblyName`**: the apps use
  `InternalsVisibleTo`, and losing it gives `CS0122: 'Program' is inaccessible`.
- **Drop a direct Alba 8.x reference** (NU1605 against Alba 9 rc).
- **Connection strings through the environment, not `UseSetting`**, for any app that ends in `RunJasperFxCommands(args)`. Alba passes
  settings as arguments, which prints `Invalid usage / Unknown argument or flag for value localhost;Port` and changes the app's
  `args.Length == 0` branches (a JasperFx/Alba interaction, not Bobcat's).
- **Set `opts.ApplicationAssembly` on every host** a v3 suite boots, including the "production static codegen" test, because the test
  exe is now the entry assembly.
- `[assembly: EventModelName(...)]` lives in namespace `Bobcat`. Its value is `opts.ServiceName`, or the entry assembly's name when
  the app sets none. The skill states neither.
- `EmitCompilerGeneratedFiles=true` to see `BobcatEventModelSource.g.cs` at all.
- `DisableAllExternalWolverineTransports()` is the right default except where routing to the broker *is* the behaviour
  (ShipmentTracking: stubbing turns `ToRabbitQueue` into a `NullSender`, which is a green test over nothing).

### 8. Out-of-process (Aspire) suites get only the recording half

The critterwatch Fleet and WebService.Http suites drive several processes over `HttpClient`. There is no in-process host for
`CritterStackSpec` or `WolverineSpec` to stand on, there is no tracked session, and without a JasperFx.Events reference there is no
Event Model. What works is `[BobcatSpec]` + step helpers + `SpecAssert`, and Fleet.PostgresqlQueues was converted that way.
What would help is an `HttpClient`-based HTTP vocabulary and an "eventually" step that renders its attempts. That is a design
question, so it is not filed.

## Not filed here (other repositories)

- **WolverineFx.Bobcat (wolverine)**: everything in trend 3.
- **ai-skills** (`critterstack-sdd-bobcat-authoring`):
  - The headline example doesn't compile on `WolverineHttpSpec`: `WhenPosted(cmd, template, routeValue)` and
    `ThenTheResponseStatusIs` belong to BankAccountES's own base class. Every port that started from it hit it.
  - Mark #456 / #540 as "after 0.60.4".
  - The csproj template: `<Using Include="Xunit" />`, keep `AssemblyName`, no direct Alba.
  - Namespaces for `EventModelName` / `BobcatFeature` / `BobcatSpec` / `WolverineHttpSpec`.
  - `EventModelName` when there is no ServiceName. `EmitCompilerGeneratedFiles`.
  - Environment variables over `UseSetting`, and `ApplicationAssembly` on every host.
  - `BOBCAT_SPEC_CONSOLE=1` plus the test exe for agents, and `BOBCAT_MONITOR=0` on shared machines.
  - Which steps are async.
  - The transport-stubbing caveat.
  - A document-only app section, and Testcontainers fixtures being fine.
  - What Bobcat offers an Aspire suite.
- **CritterStackSamples**: ProjectManagement's `AdminAssigned(command.Name)` bug.
- **JasperFx / Alba**: `UseSetting` values reach `RunJasperFxCommands` as arguments.
