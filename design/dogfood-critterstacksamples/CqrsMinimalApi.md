# CqrsMinimalApi
- Tests before: 6 (2 files: `StudentEndpointTests.cs` 5 tests, `ProductionStaticCodegenTests.cs` 1 test; xUnit v2.9.2 on VSTest, Alba 8.5.3, Shouldly)
- Specs after: 5 projected specs on `WolverineHttpSpec` + 1 plain xUnit v3 `[Fact]` kept (production static-codegen boot test, not a spec). 6/6 passing, 0 skipped.
- App: document-only Marten (no event sourcing), Wolverine.HTTP. Bumped WolverineFx.* 6.30.0 -> 6.51.0 (Marten 9.50.3, JasperFx 2.90.1 came transitively). App already net10.0.

## Adoption steps (what it took)
1. Test csproj rewritten to the skill's template: `OutputType=Exe`, `TestingPlatformDotnetTestSupport`, `UseMicrosoftTestingPlatformRunner`, `Microsoft.NET.Test.Sdk` 17.14.1, `xunit.v3` 4.0.2, `xunit.runner.visualstudio` 4.0.1, `Bobcat.Xunit`/`Bobcat.Generators` 0.60.4, `WolverineFx.Bobcat.Http` 6.51.0-alpha.1, Shouldly. Removed `coverlet.collector`, `xunit` 2.x.
2. **Had to delete the direct `Alba` 8.5.3 reference**: restore fails `error NU1605: Warning As Error: Detected package downgrade: Alba from 9.0.0-rc.2 to 8.5.3 ... CqrsMinimalApi.Tests -> WolverineFx.Bobcat.Http 6.51.0-alpha.1 -> Alba (>= 9.0.0-rc.2)`. So adopting WolverineFx.Bobcat.Http drags a *prerelease* Alba into the consumer.
3. **Had to re-add `<Using Include="Xunit" />`** — the skill's csproj omits it, and every `[Fact]`, `IAsyncLifetime`, `[Collection]` then fails CS0246. (The original v2 csproj had it; the skill template silently drops it.)
4. `global.json` `{ "test": { "runner": "Microsoft.Testing.Platform" } }` at the sample root. No BOBCAT037 (package props did the interceptor opt-in, as advertised).
5. `TestSupport.cs`: the skill's `AppFixture` / `ICollectionFixture` / abstract `StudentSpec : WolverineHttpSpec, IAsyncLifetime` with `ResetAsync()` — worked verbatim (`DisableAllExternalWolverineTransports`, `RunWolverineInSoloMode`, `MartenDaemonModeIsSolo`, `ApplicationAssembly`).
6. `AssemblyAttributes.cs`: `[assembly: EventModelName("CqrsMinimalApi")]` — the attribute is in namespace **`Bobcat`**; the skill never says so (I guessed `JasperFx.Events.EventModeling` first, CS0246).
7. Connection string: first tried Alba `x.UseSetting("ConnectionStrings:Marten", …)`. That made `Program`'s `RunJasperFxCommands(args)` print **`Invalid usage / Unknown argument or flag for value localhost;Port`** plus the full `run` help on every host boot (Alba passes settings as command-line args), and it also turned off the sample's `if (args.Length == 0) SeedData` branch. Tests still passed, so it is easy to miss. Switched to a `[ModuleInitializer]` that sets `ConnectionStrings__Marten` env var. Worth a line in the skill's fixture section for any app using `RunJasperFxCommands`.
8. Ran with `BOBCAT_MONITOR=0` after the first run, because a `bobcat ui`/console was already on :5525 serving another agent's model (see diagnostics).

## Diagnostics, crashes, surprising renders
- **xUnit v3 entry-assembly breaks the Production/static-codegen test** (severity: medium, migration trap). `ProductionStaticCodegenTests` failed after the move to xUnit v3:
  `Wolverine.Runtime.Handlers.MissingPreBuiltTypesException : Wolverine.Http is running in TypeLoadMode.Static, but 6 expected pre-built HTTP endpoint type(s) could not be loaded from the configured ApplicationAssembly 'CqrsMinimalApi.Tests'`. Cause: under xUnit v3 the test exe is the entry assembly. Fix: `builder.ConfigureServices(s => s.ConfigureWolverine(o => o.ApplicationAssembly = typeof(Program).Assembly))` in that test too. The skill mentions `ApplicationAssembly` only inside its fixture; every *other* host a suite boots needs it too (Wolverine's own message is excellent).
- **`WhenDeleted` always sends a JSON body, and a body-less `[FromQuery]` Wolverine DELETE endpoint answers 404 to it** (severity: high for HTTP CRUD apps). `WhenDeleted(new { }, $"/student/delete?id={id}")` →
  ```
  ✓ When  <>f__AnonymousType0 is sent as a DELETE to "/student/delete?id=2"
  ✗ Then  the response is 204   ✗ status: expected '204', got '404'
  ```
  Probe in the same host: raw Alba `Delete.Url(...)` → 204, `Delete.Json(new{}).ToUrl(...)` → 404. There is no body-less DELETE act at all (every `WhenDeleted*` takes `object command`). Workaround: own act + own status step on the base class (re-declared `[EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "DELETE")]`, `WhenTracked` + raw Alba, `SpecAssert.Check("status", …)`), because `ThenResponseIs` can't see a response `WhenTracked` produced.
- **Shared :5525 console** (severity: low/medium): first `dotnet test` printed `Bobcat did not publish the Event Model half of 'CqrsMinimalApi.Tests': this console is serving a model named 'CritterCrush' ... Name them alike with [assembly: EventModelName("CritterCrush")] on CqrsMinimalApi.Tests.` The advice is wrong when the console belongs to a different application (another agent's `bobcat ui`); renaming my model to CritterCrush is never the fix. The run events themselves presumably still went to that foreign console (not verified); I used `BOBCAT_MONITOR=0` from then on.
- **Passing specs print nothing** when the exe/`dotnet test` output is redirected (not a TTY) — even with `--output detailed`. Needed `BOBCAT_SPEC_CONSOLE=1`. The skill says "In a terminal with no console listening, the run prints each specification at process exit" — true, but an agent never runs in a terminal; an agent reading the skill expects to see the tree.
- **`WhenPosted<TResponse>` renders a table with an empty first row** (cosmetic bug):
  ```
  ✓ When  CreateStudentRequest is posted to "/student/create" (127ms)
  │ # │ message              │ values                                   │
  │ 1 │                      │                                          │
  │ 2 │ CreateStudentRequest │ Name: "Alice Test", Address: "100 Test…  │
  ```
  `WhenPut(cmd, route)` renders the command inline in the sentence instead — two shapes for the same kind of act.
- `GivenReadModel<Student>(student)` renders `Given the Student read model is Student(Id: 0, …)` — the id is rendered before Marten's HiLo assigns it (later steps say `there is no Student 2`). Also "read model" for a plain document.
- Every `ThenDocument`/`ThenNoReadModel` adds an extra `✓ And the Student read model has caught up` step for a document that no projection builds — noise.
- `ThenMatches(string[], string[])` renders odd cells: `✓ String[]: String[]`, `✓ [0]: Amy Second`, `✓ value.Count: 2`.
- `SpecAssert.Fact(student.Id > 0, "...")` directly in the test body (no step open) is **not rendered at all** when it passes (Rights: 5, no line). Documented in Bobcat's CLAUDE.md ("a passing check outside a step stays silent"), not in the skill; it reads like the assertion vanished.
- `BOBCAT_RESULTS=<dir>` wrote nothing: the results file (bobcat#540) is not in 0.60.4. (Optional step; noting it because Bobcat's own CLAUDE.md describes it as present.)

## Vocabulary used / missing
Base: `WolverineHttpSpec`. Used: `WhenPosted<TResponse>(command)` (route found by type), `WhenPut(command, $"/student/update/{id}")`, `WhenReading<T>(route, values)` incl. a query string template `"/student/get-by-id?id={id}"`, `ThenResponseIs`, `ThenNotFound`, `GivenReadModel<T>`, `ThenDocument<T>(id, Specify<T>()…)`, `ThenNoReadModel<T>(id)`, `ThenMatches`, `ResetAsync`. The HTTP exchange report table on failure is genuinely useful.
Gaps / friction:
- **Body-less DELETE** (above) — wrote:
  ```csharp
  [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "DELETE")]
  public async Task WhenDeletedWithoutABody(string route)
  { IScenarioResult? r = null;
    await WhenTracked($"DELETE {route}", async () => r = await AlbaHost.Scenario(x => { x.Delete.Url(route); x.IgnoreStatusCode(); }));
    _bodylessStatus = r?.Context.Response.StatusCode; }
  [Then("the DELETE answered {status}")] public void ThenTheDeleteAnswered(int status) => SpecAssert.Check("status", _bodylessStatus, status);
  ```
- **`GivenReadModel<T>(object document)` cannot infer `T`** (CS0411) because the parameter is `object`; every call must restate the type: `GivenReadModel<Student>(student)`.
- **`Specify<T>().With(x => x.Address, null)` is CS0121 ambiguous** between `With<TValue>(…, TValue)` and `With<TValue>(…, Specified<TValue>)`; needs `(string?)null`. Asserting a member is null is common.
- **`Expect.Exactly / InAnyOrder / Containing / NoneOf / Rows` do not exist in Bobcat 0.60.4** (`error CS0117: 'Expect' does not contain a definition for 'Exactly'`). Only `Expect.Value(...).Ignoring/Approximately` ship; `Within`/`InOrder` are missing too. They are on Bobcat main (a52b748, #456) after the v0.60.4 tag. Fell back to `ThenMatches(names, new[] {...})`.
- No "document" vocabulary distinct from read models (`GivenDocument`), so document CRUD specs read as "read model" everywhere.
- The int HiLo id: writing the spec against the id the Given assigned works because `GivenReadModel` mutates the passed instance — nice, but undocumented.

## Event Model and manifest
- Manifest (`BOBCAT_LIST_SPECS`): all 5 specs, identities as expected (`Student endpoints/can create a student`, …), the plain `[Fact]` correctly absent.
- `BobcatEventModelSource.g.cs` needs `-p:EmitCompilerGeneratedFiles=true` (and `--no-incremental` the first time) to exist on disk; the brief/skill imply it is just there.
- Model named `CqrsMinimalApi` (EventModelName honoured). Slices:
  - `CreateStudentRequest`: Command, `CommandType` = CreateStudentRequest, Http trigger, **no route** (by-type `WhenPosted` has nothing constant to read; no BOBCAT038 visible in build output since it is info).
  - `UpdateStudentRequest`: Command, PUT `/student/update/{id}` — resolved from the interpolated route against `[WolverinePut]`. Correct.
  - `GET /student/get-by-id`: Query, ReadsFrom Student, route **`/student/get-by-id?id={id}`** — the query string is kept in `HttpRoute`/`Label`, which will not equal Wolverine's chain route `/student/get-by-id` (likely a route SourceDisagreement on merge).
  - **`GET /student/get-all`: wrong — `View`, no trigger, no route.** Cause isolated by experiment: `WhenReading<Student[]>` and `WhenReading<List<Student>>` are not recognised as a read act (collection type argument stamps nothing), so the slice falls back to the `GivenReadModel`'s read-model role → View. Changing only the type argument to `WhenReading<Student>` produced `Query`, GET `/student/get-all`, ReadsFrom Student. A list endpoint is the most common query, so this mislabels most Query slices.
  - `DELETE /student/delete`: View (from `GivenReadModel`), no trigger — my custom act has no Command role because there is no command type; expected, but a delete reads as a View. Would need `Pattern = "Command"`.

## ai-skills gaps
- Skill's spec example does not compile on `WolverineHttpSpec`: `WhenPosted(new WithdrawFunds(accountId, 200), "/api/accounts/{accountId}/withdrawals", accountId)` — the third parameter is `Action<Scenario>`, there are no route values on `WhenPosted`; and `ThenTheResponseStatusIs(400)` does not exist (it is `ThenResponseIs`). Both come from BankAccountES's own hand-written base, but the skill presents them as the vocabulary.
- Skill claims `Expect.Exactly/InAnyOrder/Containing/NoneOf/NoItems/Rows`, `Within`, `InOrder` for "Bobcat 0.60.x"; none ship in 0.60.4.
- csproj template omits `<Using Include="Xunit" />`, and keeps a direct `Alba` reference that conflicts with WolverineFx.Bobcat.Http 6.51.0-alpha.1's Alba 9.0.0-rc.2 (NU1605).
- No namespace given for `EventModelName` (`Bobcat`), `BobcatFeature` (`Bobcat`), `BobcatSpec` (`Bobcat.Xunit`), `WolverineHttpSpec` (`Wolverine.Bobcat.Http`).
- "Passing specs print at exit" is terminal-only; agents need `BOBCAT_SPEC_CONSOLE=1` — should be the first thing in "Running specs".
- `ApplicationAssembly` must be set on every host a v3 suite boots (static codegen test), not only the fixture.
- Alba `UseSetting` + `RunJasperFxCommands` → "Invalid usage" noise and changed `args` branches; recommend env vars.
- Nothing on document-only (no event store) apps: `GivenReadModel`/`ThenDocument` are the right steps there, and `WhenDeleted` cannot drive a body-less DELETE.

## Candidate issues
- WolverineFx.Bobcat (wolverine): `WhenDeleted` needs a body-less form (`WhenDeleted(string route, params object[] routeValues)`); sending `{}` makes a `[FromQuery]` DELETE endpoint 404.
- WolverineFx.Bobcat (wolverine): WolverineFx.Bobcat.Http 6.51.0-alpha.1 depends on prerelease Alba 9.0.0-rc.2 → NU1605 for any consumer pinning Alba 8.x.
- Bobcat: generator does not treat `WhenReading<T[]>` / `WhenReading<List<T>>` as a read act → list-query slices become View with no trigger/route.
- Bobcat: a read act's route keeps the query string (`/student/get-by-id?id={id}`) in `HttpRoute`; strip it to the path template.
- Bobcat: `GivenReadModel<T>(object)` can't infer `T`; add `GivenReadModel<T>(T document)`.
- Bobcat: `Specified<T>.With(x => x.Prop, null)` ambiguous (CS0121); disambiguate overloads.
- Bobcat: `WhenPosted<TResponse>` act table renders an empty row 1.
- Bobcat: "Event Model not published" notice tells you to rename your model to the foreign console's model name; should say a different app's console holds the port.
- Bobcat: `GivenReadModel` renders the instance before the store assigns its id (`Id: 0`).
- ai-skills: fix the `WhenPosted(..., accountId)` / `ThenTheResponseStatusIs` example; mark `Expect.Exactly` & co as post-0.60.4; add `<Using Include="Xunit" />`; drop direct Alba ref; give namespaces; `BOBCAT_SPEC_CONSOLE=1` for agents; `ApplicationAssembly` on every host; env vars over `UseSetting`.
