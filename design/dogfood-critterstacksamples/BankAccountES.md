# BankAccountES
- Tests before: 10 (1 file `Tests/BankAccountTests.cs`, xUnit v2 2.9.2 + VSTest + Alba 8.5.3 + Shouldly, per-test `AlbaHost.For<Program>()` + `CleanAllMartenDataAsync`)
- Specs after: 10 (1 file `BankAccountSpecs.cs` on `WolverineHttpSpec`), **10 passing / 0 failing / 0 skipped**, green on the first run after compiling.

## Adoption steps (what it took)
1. App: WolverineFx.* 6.32.0 -> 6.51.0, Marten 9.31.1 -> 9.50.3 (WolverineFx.Marten 6.51.0 requires it). App stayed on **net9.0**; the net10.0 test project referencing it was fine. Added `public partial class Program;` (not strictly needed: the app already had `InternalsVisibleTo BankAccountES.Tests`).
2. Test csproj rewritten: `OutputType=Exe`, net10.0, `TestingPlatformDotnetTestSupport` + `UseMicrosoftTestingPlatformRunner`, packages `Microsoft.NET.Test.Sdk 17.14.1`, `xunit.v3 4.0.2`, `xunit.runner.visualstudio 4.0.1`, `Bobcat`/`Bobcat.Xunit`/`Bobcat.Generators 0.60.4`, `WolverineFx.Bobcat` + `WolverineFx.Bobcat.Http 6.51.0-alpha.1`, **`Alba 9.0.0-rc.2`** (WolverineFx.Bobcat.Http 6.51.0-alpha.1 depends on Alba 9.0.0-rc.2, so the sample's Alba 8.5.3 has to move to a prerelease), Shouldly kept (unused in the end). No InterceptorsNamespaces needed (the package's buildTransitive props did it; no BOBCAT037).
3. `global.json` `{ "test": { "runner": "Microsoft.Testing.Platform" } }` beside the .sln.
4. `TestSupport.cs`: `AppFixture : IAsyncLifetime` (Alba host, `DisableAllExternalWolverineTransports`, `RunWolverineInSoloMode`, `opts.ApplicationAssembly`), `[CollectionDefinition]`, abstract `BankAccountSpec(AppFixture) : WolverineHttpSpec(app.Host), IAsyncLifetime` with `ResetAsync()` in `InitializeAsync`. Needed `using Xunit;` (the old csproj's `<Using Include="Xunit"/>` went with the rewrite; the skill's TestSupport snippet omits the using).
5. `AssemblyAttributes.cs`: `[assembly: EventModelName("BankAccount")]` (= `opts.ServiceName`) and `Parallelization(Mode = None)`.
6. Connection string: `ConnectionStrings__Marten` set from the fixture via `Environment.SetEnvironmentVariable` (see diagnostics: `UseSetting` produced JasperFx "Invalid usage").
7. `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` to be able to read `BobcatEventModelSource.g.cs` at all (brief/skill say "look in obj/**/generated" but nothing is written there without it).

## Diagnostics, crashes, surprising renders
- **Skill's headline example does not compile on WolverineHttpSpec** (severity: high for an outside agent). `await WhenPosted(new WithdrawFunds(accountId, 200), "/api/accounts/{accountId}/withdrawals", accountId);` -> the third parameter of `WolverineHttpSpec.WhenPosted(object, string, Action<Scenario>?)` is `configure`, so a Guid does not bind; and `ThenTheResponseStatusIs(400)` does not exist (it is `ThenResponseIs(400)`). Both come from the in-repo BankAccountES sample's *own* `CritterStackSpec`-based base class, not from WolverineHttpSpec, but the skill presents them as the vocabulary.
- **`WhenPosted(command, route)` posts the route literally** — a template like `"/api/accounts/{accountId}/deposits"` is NOT filled (only `WhenReading<T>(route, params values)` fills). Use the by-type form `WhenPosted(cmd)` (route filled from the command's properties) or an interpolated string. Not documented; easy to get a 404.
- **`Expect.Exactly` / `Expect.InAnyOrder`: CS0117 'Expect' does not contain a definition for 'Exactly'** (high). Collection expectations (bobcat#456, commit a52b748) landed on main AFTER the v0.60.4 tag; the skill documents them as Bobcat 0.60.x. Workaround: index into the list and `ThenMatches` each element (`ThenMatches(txns.Transactions[0], Specify<Transaction>()...)`).
- **`BOBCAT_RESULTS` does nothing on 0.60.4** — results file (bobcat#540) is also post-0.60.4. No file, no warning.
- **`x.UseSetting("ConnectionStrings:Marten", …)` in `AlbaHost.For<Program>`** reaches the entry point as args and the app's `RunJasperFxCommands(args)` prints `Invalid usage / Unknown argument or flag for value localhost;Port` plus the whole `run` usage table into the test output. Tests still pass (Alba already captured the host). Workaround: env var. Also every run prints 8 lines of `Searching '…' for commands` (JasperFx command discovery) into each test's output. Severity: low (noise), JasperFx/Alba.
- **Running the test exe directly published to someone else's console**: `Bobcat did not publish the Event Model half of 'BankAccountES.Tests': this console is serving a model named 'CritterCrush'…` — a `bobcat ui` on :5525 from another session received our run. Default-on probing of 5525 on a shared machine; set `BOBCAT_MONITOR=0`. The message's advice ("Name them alike with [assembly: EventModelName("CritterCrush")]") is wrong advice in this situation (low).
- `dotnet test` prints no specification tree on success (expected; captured). Failure output is good: gathered `SpecAssertionException : 1 specification step failed: And the Account matches => Balance: expected '701', got '700'` plus the tree, Named values, HTTP exchanges and Events appended tables. 
- Surprising renders (low):
  - An inline `Guid.NewGuid()` passed as `ClientId` renders as `ClientId: Client` (and Named values `Client | da85…`), e.g. `AccountOpened(AccountId: accountId, ClientId: Client, Currency: "USD")`.
  - `WhenReading<List<Account>>` renders `When List`1 is read from "/api/clients/{clientId}/accounts"`.
  - `ThenMatches(list.Count, 2)` renders `Then the Int32 matches ✓ Int32: Int32 ✓ value: 2`.
  - `Then a Account stream is started` ("a Account").
  - POST/PUT steps show the filled URL (`…/accounts/e9f2eecf-…/deposits`), GET steps show the template — inconsistent.

## Vocabulary used / missing
- Used: `GivenEvents`, `GivenEventsOn`, `GivenContext`, `WhenPosted(cmd)`, `WhenPosted<T>(cmd)`, `WhenPut(cmd)`, `WhenReading<T>(route, values)`, `ThenResponseIs`, `ThenEvents`, `ThenNoEvents`, `ThenRefusedWith("Insufficient funds")` (matched the ProblemDetails body), `ThenStreamIsStartedWithEvents<T>`, `TheStartedStream<T>`, `ThenAggregate<T>`, `ThenMatches`, `Specify<T>().With`.
- No fallback to raw Alba/Shouldly was needed. Gaps: collection matching (see above, worked around with per-element `ThenMatches`); no "count" step (`ThenMatches(x.Count, 2)` renders badly).

## Event Model and manifest
- Manifest (`BOBCAT_LIST_SPECS`): all 10 specs, identities as expected (`Bank Account/deposit funds`, …, `Bank Account/withdraw insufficient funds returns 400`). Correct.
- `BobcatEventModelSource.g.cs`: model name `BankAccount` (good). Command slices EnrollClient/UpdateClient/OpenAccount/DepositFunds/WithdrawFunds have the right command, event, aggregate; OpenAccount gets `ReadsFrom = Client` from `GivenContext` (good). Query slices for transactions and client: `Pattern = Query`, route + GET, `ReadsFrom` right.
- **Wrong 1: by-type HTTP acts put no route on the slice.** `WhenPosted(cmd)` / `WhenPut(cmd)` give `TriggerKind = Http` but no `TriggerOrigin` (no route, no verb), even though exactly one endpoint accepts the type. With `WhenPosted(cmd, $"/api/accounts/{accountId}/deposits")` the slice gets `HttpRoute = "/api/accounts/{accountId}/deposits", HttpMethod = "POST"`. Also `WolverineHttpSpec`'s four `WhenPosted` overloads carry `[EventModelTrigger(TriggerKind.Http)]` with no `HttpMethod = "POST"` (PUT/PATCH/DELETE ones have their verb; the `WolverineHttpScenario` copies do have POST).
- **Wrong 2: `WhenReading<List<Account>>(…)` is not a read act.** Slice `GET /api/clients/{clientId}/accounts` has no Pattern, no TriggerKind, no TriggerOrigin, and `AggregateTypes = Account` (from `GivenEventsOn<Account>`) — i.e. a collection `T` stamps no ReadModel role so the GET trigger is dropped and the slice is not a Query. The in-repo port dodges it by writing `WhenReading<Account>(…)` against a list endpoint (and then can't use the result).

## ai-skills gaps
- Main example uses `WhenPosted(cmd, template, routeValue)` and `ThenTheResponseStatusIs` — neither exists on WolverineHttpSpec (they are the in-repo sample's own acts on CritterStackSpec).
- `Expect.Exactly/InAnyOrder/Containing/NoneOf/Rows`, "a bare collection member is a set", and the results file are documented as 0.60.x but are not in the published 0.60.4.
- Doesn't say `WhenPosted(cmd, route)` takes a *filled* route (interpolate), unlike `WhenReading`.
- Doesn't say by-type acts lose the route on the Event Model (prefer an interpolated route if the model matters).
- TestSupport snippet needs `using Xunit;` (and `using Wolverine;` for `ConfigureWolverine`/`RunWolverineInSoloMode`); "look in obj/generated" needs `EmitCompilerGeneratedFiles`.
- Doesn't mention WolverineFx.Bobcat.Http pulls Alba 9.0.0-rc.2.
- Brief/skill: WolverineFx.Bobcat source lives at `~/code/wolverine/src/Bobcat/Wolverine.Bobcat*`, not `src/Testing/WolverineFx.Bobcat`.

## Candidate issues
- ai-skills: fix the headline example (`WhenPosted(cmd)` / interpolated route, `ThenResponseIs`), and mark #456 collection expectations + results file as "after 0.60.4".
- Bobcat: release main (collection expectations #456, results file #540) — the skill already teaches them.
- Bobcat (generator): resolve a by-type HTTP act's route from the one endpoint whose request type matches (`WhenPosted(cmd)` / `WhenPut(cmd)` currently stamp no TriggerOrigin).
- Bobcat (generator): `WhenReading<List<T>>` / `IReadOnlyList<T>` should be a read act (ReadModel = element type) so the slice is a Query.
- WolverineFx.Bobcat (wolverine): add `HttpMethod = "POST"` to `WolverineHttpSpec.WhenPosted` overloads' `[EventModelTrigger]`.
- WolverineFx.Bobcat (wolverine): `WhenPosted/WhenPut(cmd, route, …)` should fill a `{param}` template from the command (or accept route values like `WhenReading`) instead of posting it literally.
- Bobcat (rendering): `ClientId: Client` naming for inline `Guid.NewGuid()`; `List\`1`; `the Int32 matches ✓ Int32: Int32`; "a Account".
- Bobcat: the "console serving another model" notice should not advise renaming the model when the console simply belongs to someone else.
