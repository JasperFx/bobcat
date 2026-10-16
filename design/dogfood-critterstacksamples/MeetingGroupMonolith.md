# MeetingGroupMonolith
- Tests before: 10 (1 file `Tests/MeetingGroupTests.cs`, xUnit v2 + VSTest, Alba + Shouldly, per-test AlbaHost, `Task.Delay(1000)` to wait for cascades)
- Specs after: 13 (all 10 ported + 3 new: unknown proposal 404, full meeting 400, GET by id), **13 passing / 0 failing / 0 skipped** (`dotnet test --project Tests`, ~4 s)

## Adoption steps (what it took)
1. App: bumped every `WolverineFx.*` 6.30.0 -> 6.51.0 (pulls Marten 9.50.3). App stayed on **net9.0**; test project net10.0. No app code changes (top-level `Program` is internal; the existing `InternalsVisibleTo Include="Tests"` was enough for `AlbaHost.For<Program>`).
2. `global.json` at the sample root: `{ "test": { "runner": "Microsoft.Testing.Platform" } }`.
3. Tests.csproj rewritten: `OutputType=Exe`, net10.0, `UseMicrosoftTestingPlatformRunner`, `TestingPlatformDotnetTestSupport`, `Microsoft.NET.Test.Sdk 17.14.1`, `xunit.v3 4.0.2`, `xunit.runner.visualstudio 4.0.1`, `Bobcat`/`Bobcat.Xunit`/`Bobcat.Generators 0.60.4`, `WolverineFx.Bobcat` + `WolverineFx.Bobcat.Http 6.51.0-alpha.1`, `Alba 9.0.0-rc.2` (forced by WolverineFx.Bobcat.Http), Shouldly. Dropped coverlet + xunit 2.
   - Package path: no InterceptorsNamespaces / BOBCAT037 work needed (buildTransitive props did it).
   - Had to add `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` to see `BobcatEventModelSource.g.cs` at all (the brief/skill point at `obj/**/generated/**`, which does not exist without it).
4. Lost the old `<Using Include="Xunit" />`: xunit.v3 has no implicit `using Xunit`, so `IAsyncLifetime`/`CollectionDefinition` failed to compile until I added `using Xunit;`.
5. `TestSupport.cs`: collection fixture booting `AlbaHost.For<Program>` with `UseSetting("ConnectionStrings:Marten", …)` (5445 db), `DisableAllExternalWolverineTransports`, `RunWolverineInSoloMode`, `opts.ApplicationAssembly`; base `MeetingGroupsSpec : WolverineHttpSpec(app.Host), IAsyncLifetime` calling `ResetAsync()`; `[assembly: EventModelName("MeetingGroups")]` (= `opts.ServiceName`), `[assembly: Xunit.v3.Parallelization(Mode = ParallelMode.None)]`.
6. Did NOT need `WebApplicationFactoryContentRoot` (the in-repo port needs it; with Alba 9 rc.2 + the sample's layout it booted fine).

## Diagnostics, crashes, surprising renders
1. **`Expect.Exactly(...)` does not exist in Bobcat 0.60.4** — `error CS0117: 'Expect' does not contain a definition for 'Exactly'`. In the 0.60.4 package `Bobcat.Expect` has only `Value<T>` (checked: git `v0.60.4:src/Bobcat/Partial/Expectations.cs`, and `Exactly/InAnyOrder/Containing/NoneOf/NoItems/Rows` are absent from the dll). Collection expectations (bobcat#456) are on main only. Skill says they are 0.60.x. Severity: high (skill teaches an API the published package lacks).
2. **Member checks reject anything but `member.ShouldX(...)`**, at run time: `System.ArgumentException : 'x => x.Attendees.ShouldHaveSingleItem(null).MemberId' is not a member path. Name a property or field of the object, such as x => x.Name or x => x.Address.City. (Parameter 'expression')`. It compiles, and it is an **Error** (ends the test), not a gathered wrong. Same for `Specify<List<MeetingGroup>>(x => x.ShouldHaveSingleItem().Name.ShouldBe("…"))`. Workaround: `Specify<Meeting>(x => x.Attendees.Count.ShouldBe(1))` then `ThenMatches(stored.Attendees[0], Specify<MeetingAttendee>().With(...))`. Severity: medium (would be a good compile-time diagnostic; skill doesn't say chained Shouldly is unsupported).
3. **A `protected` `[Then]` helper breaks the build in generated code**: `obj/.../BobcatStepInterceptors.g.cs(23,33): error CS0122: 'MeetingGroupsSpec.TheOnlyItem<T>(IReadOnlyList<T>?)' is inaccessible due to its protection level`. Making it `public` fixed it. No BOBCAT diagnostic; nothing in the skill says step helpers must be public/internal. Severity: medium (CS error in a file the user cannot edit).
4. **BOBCAT027 for a type-parameter placeholder, reported at the call site**: `MeetingSpecs.cs(57,9): warning BOBCAT027: Step attribute on 'ThenACascadeAppended' has the placeholder '{T}', but the method has no parameter named 'T' (it takes matching).` `{T}` for a generic step is a natural thing to want; and the warning points at the caller, not the attribute. Low.
5. **`ThenSingleDocument<T>` does not work for plain documents**: `SpecCriticalException : ThenSingleDocument<MeetingGroup>() reads the stream the scenario is about, but the act started none and the givens arranged none. Name it: ThenReadModel<MeetingGroup>(id, ...).` The id is minted by a cascaded handler, so it can't be named. Name reads as "the single document of type T"; it is not. Workaround: query `IQuerySession` by hand. Medium (vocabulary gap, see below).
6. **Renders**:
   - Anonymous body: `✓ When  <>f__AnonymousType0 is posted to "/api/administration/proposals/75273a49-…/accept"` (and in the timing summary `step <>f__AnonymousType0 is posted to {string} cost 714ms`).
   - Generic read type: `✓ When  List`1 is read from "/api/meeting-groups"`.
   - `meeting.Id` renders as the *named value* `Meeting`, which reads like a type: `When  AddAttendee(MeetingId: Meeting, MemberId: memberId) is posted to …`, and `MISSING MeetingFeeCreated(PayerId: memberId, MeetingId: Meeting)`.
   - `GivenReadModel<MeetingGroup>(group)` renders `Given the MeetingGroup read model is MeetingGroup` (no values).
   - `ThenDocument<User>` on a plain (non-projected) document still emits `And the User read model has caught up` before every document read: noise for a document app.
   - The host's Marten DDL/info logs are dumped into captured output of the run (22 `info:` blocks) — app-side, but drowns the spec render in `dotnet test`.
7. **Run published to somebody else's console**: plain `dotnet test` found a `bobcat ui` on :5525 and printed `Error output: Bobcat did not publish the Event Model half of 'Tests': this console is serving a model named 'CritterCrush' and the half is named 'MeetingGroups'. … Name them alike with [assembly: EventModelName("CritterCrush")] on Tests.` The advice is wrong for this case (it tells me to rename my model to an unrelated app's), and the run events still went to that console. I used `BOBCAT_MONITOR=0` afterwards. Medium (shared-machine pollution + misleading fix).
8. `BOBCAT_RESULTS=<dir>` writes nothing on 0.60.4 (ResultsFile #540 landed after the tag). Not a bug, but the brief/CLAUDE.md expectation doesn't hold for package consumers yet.

## Vocabulary used / missing
Base: `WolverineHttpSpec`. Used: `WhenPosted(cmd)`, `WhenPosted(cmd, route)`, `WhenPosted<T>(cmd[, route])`, `WhenReading<T>(route, values)`, `GivenReadModel<T>`, `ThenResponseIs`, `ThenNotFound`, `ThenRefusedWithProblem(Specify<ProblemDetails>()…)`, `ThenRefusedWith("Password")`, `ThenDocument<T>(id, Specify…)`, `ThenNoReadModel<T>()`, `ThenMatches`, `ThenMessageSent(Specify<…>)`, `ThenNoMessageSent<T>`, `ThenStreamIsStartedWithEvents<T>`, `ThenAggregate<T>`, `ThenNoEvents`, member checks `Specify<T>(x => x.Status.ShouldBe(...), x => x.DecisionDate.ShouldNotBeNull())`. The vocabulary removed both `Task.Delay(1000)`s (tracked session waits for the durable local-queue cascades) and all propose+accept arrange chains (`GivenReadModel` instead).
Gaps (code written instead):
- **POST with no body**: the accept endpoint takes only a route id. Had to `WhenPosted(new { }, route)` — ugly render and the slice loses its command/trigger (see Event Model). Want `WhenPosted(route, params routeValues)`.
- **WhenPosted(command, route) has no route-values overload** (unlike `WhenReading`), so `"/api/administration/proposals/{id}/accept".Replace("{id}", …)` or interpolation.
- **Cascade events**: `ThenEmitted(Specify<MeetingFeeCreated>()…)` → `MISSING` because the fee stream is started by a cascaded handler (in `LastAct.AllNewEvents`, not `NewEvents`). The MISSING message does not hint that a matching event *was* appended by a cascade. Wrote:
  ```csharp
  [Then("a cascaded handler appended a matching event")]
  public void ThenACascadeAppended<T>(Func<T, bool> matching)
      => SpecAssert.Fact(LastAct.AllNewEvents.Any(e => e.Data is T t && matching(t)), $"no {typeof(T).Name} …");
  ```
- **Lists**: no step for "the list holds exactly one X" / list members as partials (Expect.* not shipped). Wrote `TheOnlyItem<T>(IReadOnlyList<T>)` with `SpecAssert.Check("count", …, 1)` + `ThenMatches(item, Specify…)`.
- **Find a document by predicate / the only document of T**: wrote `AllDocuments<T>()` over `IDocumentStore.QuerySession()`.
- No step to assert a response body directly (used `WhenPosted<T>` return + `ThenMatches`) — fine.

## Event Model and manifest
- Manifest (`BOBCAT_LIST_SPECS`): all 13 specs, identities as expected (`Registrations/a registered user is stored and becomes a member`, …), framework xunit, lane projected. Correct.
- `BobcatEventModelSource.g.cs`, model `"MeetingGroups"`: 9 slices. Right: `RegisterUser` (Command, Http, route `/api/registrations` POST, published `NewUserRegisteredEvent`), `CreateSubscription` (Command, Aggregate `Subscription`, Emitted `SubscriptionCreated`, published `SubscriptionExpirationChangedEvent`), `GET /api/meeting-groups/{id}` (Query, route + ReadsFrom `MeetingGroup`).
- Wrong / weak:
  - **`WhenReading<List<MeetingGroup>>("/api/meeting-groups")` is not a read act**: slices `GET /api/meeting-groups` and `GET /api/meetings` come out `Pattern = View`, no TriggerKind, no route; only `ReadModelTypes = MeetingGroup` from the `GivenReadModel` arrange. The same spec shape with `WhenReading<MeetingGroup>` gives a proper Query. A collection `T` drops the role.
  - **`AcceptMeetingGroupProposal`** (anonymous body) is `Pattern = View`, no command, no trigger kind — an HTTP POST that changes state rendered as a View, because the only remaining role is `GivenReadModel`'s ReadModel.
  - **`GivenReadModel<T>` arranges stamp `ReadModelTypes` on Command slices**: `CreateMeeting` gets ReadModel `MeetingGroup`, `AddAttendee` gets `MeetingGroup, Meeting`. These are lookups (`[Entity]`), i.e. `ReadsFrom`, the document analogue of `GivenContext`.
  - **By-type HTTP acts carry no route**: `WhenPosted(new CreateSubscription(...))` → `TriggerKind = Http` but no `TriggerOrigin`; same for `ProposeNewMeetingGroup`, `CreateMeeting`, `AddAttendee`. The generator already indexes `[WolverinePost]` endpoints and could resolve the one endpoint accepting that body type.
  - Documents asserted with `ThenDocument` (User, Member, Meeting, MeetingGroupProposal) appear only in `ResolvedTypes`, so a document-centric module shows almost nothing on the canvas (by design per the docs, but worth noting for document apps).

## ai-skills gaps
- SKILL.md example uses `ThenTheResponseStatusIs(400)` — no such member; it is `ThenResponseIs(int)` on `WolverineHttpSpec`.
- SKILL.md example `WhenPosted(new WithdrawFunds(accountId, 200), "/api/accounts/{accountId}/withdrawals", accountId)` — `WolverineHttpSpec.WhenPosted(object, string, Action<Scenario>?)` takes **no route values**; that signature is BankAccountES's hand-written base. An agent copying it onto `WolverineHttpSpec` gets a compile error (accountId is not an `Action<Scenario>`).
- Skill/vocabulary present `Expect.Exactly/InAnyOrder/Containing/NoneOf/NoItems/Rows` as 0.60.x; not in 0.60.4.
- Never says which namespace `EventModelName` lives in (`Bobcat`); I first put `using JasperFx.Events.EventModeling;`.
- Doesn't mention xunit.v3 drops the implicit `using Xunit`.
- Doesn't say member checks accept only `x => x.Member(.Member)*.ShouldX(...)`, nor that step helpers must be public (CS0122 in generated code).
- Says check `obj/**/generated/**`; needs `EmitCompilerGeneratedFiles=true`.
- Doesn't warn that a plain `dotnet test` publishes to whatever console holds :5525 (shared machines, CI agents); `BOBCAT_MONITOR=0` is the off switch.
- `ThenSingleDocument` description ("the T of the stream the scenario is about") is accurate but there is no alternative listed for documents a cascade created.
- Bobcat 0.60.4 package ships no XML docs (no `Bobcat.xml` in lib/), so no IntelliSense for the whole `CritterStackSpec` vocabulary; WolverineFx.Bobcat does ship them.

## Candidate issues
- Bobcat: `WhenReading<List<T>>`/`IReadOnlyList<T>` read act is dropped by the generator → GET list slice becomes View with no route (should be Query reading T).
- Bobcat: `GivenReadModel<T>` arrange stamps `ReadModelTypes` on a Command slice; should be `ReadsFrom` (document analogue of `GivenContext`, #502).
- Bobcat: by-type HTTP act (`WhenPosted(cmd)`) gets no route on the slice; resolve via the endpoint index by body type.
- Bobcat: diagnostic (or support) for non-public `[Given]/[When]/[Then]` helpers instead of CS0122 in `BobcatStepInterceptors.g.cs`.
- Bobcat: member checks — compile-time diagnostic for non-member-path Shouldly chains (`x.List.ShouldHaveSingleItem().Prop.ShouldBe`), or support them; at least make it a gathered wrong, not an Error.
- Bobcat: BOBCAT027 should allow `{T}` (type-parameter name) and report at the attribute, not the call site.
- Bobcat: render anonymous/generic types readably (`<>f__AnonymousType0`, ``List`1``), and don't render `meeting.Id` as the bare named value `Meeting`.
- Bobcat: Event Model publish refusal tells you to rename your model to whatever unrelated model the console serves; should say "another app's console is on :5525; use BOBCAT_MONITOR_URL / BOBCAT_MONITOR=0".
- Bobcat: publish XML docs in the Bobcat package.
- Bobcat: `ThenDocument` on a non-projected document emits a "read model has caught up" step every time; skip/hide when no async projection builds T.
- Bobcat: ship collection expectations (#456) in a release, or ai-skills must say "main only".
- WolverineFx.Bobcat (wolverine): `WhenPosted(route, params routeValues)` with no body, and route-values overloads on `WhenPosted/WhenPut/…(command, route, …)` like `WhenReading`.
- Bobcat/WolverineFx.Bobcat: `ThenEmitted`/`ThenEvents` MISSING should mention a matching event in `LastAct.AllNewEvents` (appended by a cascade); and/or a `ThenCascadeAppended`/`ThenEventuallyEmitted` step that judges AllNewEvents.
- Bobcat: a step for "the one/any document of T matching a partial" (`ThenSingleDocument` only works for stream-backed reads).
- ai-skills: fix `ThenTheResponseStatusIs`, the route-values `WhenPosted` example, Expect.* availability, `EventModelName` namespace, `using Xunit`, public helpers, member-check shape, `EmitCompilerGeneratedFiles`, `BOBCAT_MONITOR=0` on shared machines.
