# ProjectManagement
- Tests before: 1 (`create_new_project.cs`, xUnit v2 + VSTest, Alba; asserted only `StatusCodeShouldBe(201)` and dumped the body)
- Specs after: 2 — 0 passing, 2 failing (both for reasons worth reporting: one Bobcat/Wolverine gap, one real application bug the original test never caught)

## Adoption steps (what it took)
- `Tests.csproj` rewritten from the skill's template: `OutputType=Exe`, net10.0, `TestingPlatformDotnetTestSupport`, `UseMicrosoftTestingPlatformRunner`, xunit.v3 4.0.2, Microsoft.NET.Test.Sdk 17.14.1, xunit.runner.visualstudio 4.0.1, Bobcat.Xunit + Bobcat.Generators 0.60.4, **WolverineFx.Bobcat.Http** 6.51.0-alpha.1 (pulls WolverineFx.Bobcat, Alba 9.0.0-rc.2). Dropped `xunit` 2.x, `coverlet.collector`, and the direct `Alba 8.5.3` reference.
- App: net9.0 -> net10.0, `Microsoft.AspNetCore.OpenApi` 9.0.0 -> 10.0.0, WolverineFx.Http.Marten + WolverineFx.RuntimeCompilation 6.30.0 -> 6.51.0 (Marten 9.50.3 comes transitively). No code changes needed in the app.
- `global.json` `{ "test": { "runner": "Microsoft.Testing.Platform" } }` beside the .sln.
- `GlobalUsings.cs`: `global using Xunit; global using Bobcat; global using Bobcat.Xunit;` — **none of these are implicit**; xunit.v3 adds no global using, so `IAsyncLifetime`, `[Collection]`, `[BobcatSpec]`, `[EventModelName]` all fail CS0246 until added. The skill's examples never show a `using`.
- `[assembly: EventModelName("ProjectManagement.Api")]` (the app sets no `ServiceName`, so Wolverine's default is the entry assembly name). Namespace is `Bobcat`, not `JasperFx.Events.EventModeling` (my first guess, from `TriggerKind`'s namespace in the skill).
- Fixture per the skill: `AlbaHost.For<Program>`, `DisableAllExternalWolverineTransports`, `RunWolverineInSoloMode`, `opts.ApplicationAssembly = typeof(Program).Assembly`, plus `JasperFxEnvironment.AutoStartHost = true` (namespace `JasperFx.CommandLine`) kept from the original. Base: `WolverineHttpSpec(app.Host)` + `IAsyncLifetime` with `ResetAsync()`.
- `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` to see `BobcatEventModelSource.g.cs` (not mentioned in the skill; nothing is in obj/ without it).
- Run with `BOBCAT_MONITOR=0` after the first run (see below).

## Diagnostics, crashes, surprising renders
1. **Connection-string override through `UseSetting` kills the host** (JasperFx/Alba, not Bobcat). `x.UseSetting("ConnectionStrings:marten", "Host=localhost;Port=5445;...")` reached `RunJasperFxCommands` as an argument: `Invalid usage / Unknown argument or flag for value localhost;Port`, then the usage table. Workaround: `Environment.SetEnvironmentVariable("ConnectionStrings__marten", …)` in the fixture before `AlbaHost.For`. Severity: medium (every sample that overrides a connection string in tests will hit it).
2. **`WhenPosted` against a Minimal API endpoint sees no events.** The endpoint is `app.MapPost(…, (CreateProject, IDocumentSession) => session.Events.StartStream…)`, i.e. Marten with `IntegrateWithWolverine()` but not a Wolverine endpoint. Result:
   ```
   ✓ When  CreateProject is posted to "/api/project/create" (62ms)
   ✓ Then  the response is 201
   ✗ And   a Project stream is started (1ms)
     The act started no Project stream; it started no stream at all.
   ```
   Cause (read in wolverine@0f65b9d, the commit the package was built from): `WolverineScenario.runActAsync` has the per-store high-water read-back fallback for a commit the tracked session never heard (GH-4977), but `WolverineHttpScenario.sendAsync` dispatches through `executeAsync` directly and never takes marks / reads back. So an HTTP act on a non-Wolverine endpoint (Minimal API, MVC, or a handler using a raw session) reports no events, no started stream, and every `ThenEvents`/`ThenNoEvents` is judged against nothing — `ThenNoEvents()` would be a **false green**. Workaround: `WhenActing("…", async _ => await host.Scenario(…))` (store read-back works; it found all 4 events) — but that stamps no HTTP trigger/Command on the Event Model. Severity: high.
3. **The read-back workaround exposed a real app bug** the original test never saw: `new AdminAssigned(command.Name)` instead of `command.Admin`:
   ```
   │ 2 │ AdminAssigned      │ expected 'UserName: <the admin email>',  │  FAIL  │
   │   │                    │ got 'UserName: Clean the house'            │        │
   ```
   (The in-repo Bobcat port's spec asserts `ThenEvents(typeof(AdminAssigned), …)` by type and `ThenEmitted` without the AdminAssigned value, so it passes over the same bug.)
4. **Odd render of the HTTP act's message table**: an empty first row above the command:
   ```
   │ # │ message       │ values │
   │ 1 │               │        │
   │ 2 │ CreateProject │ Name: "Clean the house", Admin: …
   ```
   Severity: low (cosmetic, but confusing — looks like a null message was dispatched).
5. **Some `Then…` steps are async, some are not, and a forgotten `await` is only a warning.** `ThenResponseIs` is `void` (`await` = CS4008), `ThenStreamIsStartedWithEvents` returns `Task` (no `await` = CS4014 *warning*, and the assertion runs unobserved — potentially after the scenario closed). The skill's tables never say which steps return `Task`. Suggest an analyzer/BOBCAT diagnostic (or `TreatWarningsAsErrors` for CS4014 in the generated csproj). Severity: medium (false-green risk).
6. **Shared monitor console + model-name refusal advice.** With a console on :5525 (another agent's `bobcat ui` serving CritterCrush), the run printed:
   `Bobcat did not publish the Event Model half of 'Tests': this console is serving a model named 'CritterCrush' and the half is named 'ProjectManagement.Api'. … Name them alike with [assembly: EventModelName("CritterCrush")] on Tests.`
   The refusal is right, but the advice is wrong for this case: it tells you to rename your model to whatever unrelated model the console happens to serve. It should say "or this console serves a different application; point `BOBCAT_MONITOR_URL` elsewhere / `BOBCAT_MONITOR=0`". Severity: low-medium (an agent would follow it literally).
7. `BOBCAT_RESULTS=<dir>` wrote nothing: the results file (bobcat#540) is not in 0.60.4 (`845d543` is not an ancestor of `v0.60.4`). Expected; noting because CLAUDE.md/brief describe it as available.

## Vocabulary used / missing
- `WolverineHttpSpec`: `WhenPosted(command, route)`, `ThenResponseIs(201)`, `ThenStreamIsStartedWithEvents<Project>(…)`; `CritterStackSpec.WhenActing` as the workaround.
- Missing: an HTTP act that works on a non-Wolverine endpoint (finding 2). Code written instead:
  ```csharp
  await WhenActing("CreateProject is posted to /api/project/create", async _ =>
      await app.Host.Scenario(x => { x.Post.Json(command).ToUrl("/api/project/create"); x.StatusCodeShouldBe(201); }));
  ```
- The original test's commented intent ("check the data that was appended") maps directly onto `ThenStreamIsStartedWithEvents` — that part of the vocabulary fit perfectly.
- `ThenAggregate<Project>` not used: `Project` has no `Apply` methods, so there is no aggregate to assert (app limitation).

## Event Model and manifest
- Manifest (`BOBCAT_LIST_SPECS`): both specs, identities `Create a new project/create a new project` and `…/create a new project through when acting` — as expected.
- `BobcatEventModelSource.g.cs`: model `ProjectManagement.Api`, one slice `CreateProject`, `Pattern = Command`, `TriggerKind.Http`, `HttpRoute = "/api/project/create"`, `HttpMethod = "POST"`, events ProjectCreated/AdminAssigned/TeamMemberAssigned, aggregate `Project`, both specs linked. Correct. (The `WhenActing` spec's `ResolvedTypes` lacks `CreateProject`, as expected.)
- Note the model is correct *although* the run-time act saw no events — the design-time model and the run disagree silently.

## ai-skills gaps
- SKILL.md's flagship example calls `WhenPosted(cmd, "/api/accounts/{accountId}/withdrawals", accountId)` and `ThenTheResponseStatusIs(400)` — neither exists on `WolverineHttpSpec` (it has `WhenPosted(object, string route, Action<Scenario>?)` and `ThenResponseIs(int)`). Those are BankAccountES's own `CritterStackSpec` helpers. An agent copying the example onto `WolverineHttpSpec` gets compile errors (or, worse, `accountId` bound to `configure`… it does not compile, at least).
- No `using` lines anywhere (Xunit, Bobcat, Bobcat.Xunit, Wolverine.Bobcat, Wolverine.Bobcat.Http, JasperFx.CommandLine).
- `[EventModelName]`'s namespace (`Bobcat`) not stated.
- Doesn't say which steps return `Task` (finding 5).
- Doesn't say that `WhenPosted` only sees events committed by **Wolverine** endpoints/handlers; the vocabulary says "On WolverineSpec 'the act's events' are what the sessions handling the act's own message (or HTTP request) committed", which reads as if any HTTP request counts.
- `EmitCompilerGeneratedFiles` for inspecting the generated model not mentioned.
- The Bobcat 0.60.4 package ships no XML doc file (only `Bobcat.dll`), so IntelliSense has nothing; WolverineFx.Bobcat.Http does ship one.

## Candidate issues
- WolverineFx.Bobcat (wolverine): HTTP acts (`WolverineHttpScenario.sendAsync`) should apply the same per-store high-water read-back as `runActAsync` (GH-4977) so a Minimal API / MVC / raw-session endpoint's commits are seen.
- WolverineFx.Bobcat (wolverine): HTTP act render shows an empty row 1 in the message table.
- Bobcat: model-name refusal message should offer "this console serves another app" (BOBCAT_MONITOR=0 / BOBCAT_MONITOR_URL) instead of only "rename yours to match".
- Bobcat / WolverineFx.Bobcat: diagnostic for an un-awaited `Task`-returning step (false-green risk), or make the generated csproj treat CS4014 as error.
- Bobcat: ship XML docs in the Bobcat package.
- JasperFx/Alba: `UseSetting` values containing `;` are passed to `RunJasperFxCommands` as args and rejected.
- ai-skills: fix the SKILL.md example (WhenPosted route values, ThenTheResponseStatusIs -> ThenResponseIs), add usings and namespaces, mark async steps, state the "Wolverine endpoints only" limit of HTTP acts.
