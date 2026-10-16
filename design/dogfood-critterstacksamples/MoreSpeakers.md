# MoreSpeakers
- Tests before: 10 (2 files `SpeakerEndpointTests.cs`, `MentorshipEndpointTests.cs` + `DatabaseCollection.cs`; xUnit v2 + VSTest, Alba + Shouldly, an AlbaHost per test, seeding through `LightweightSession()`)
- Specs after: 10 (all ported), **10 passing / 0 failing / 0 skipped**, green on the first run.

## Adoption steps (what it took)
1. App: `WolverineFx.*` 6.30.0 -> 6.51.0, **`Marten.AspNetCore` 9.29.0 -> 9.50.3** (must match the Marten WolverineFx.Marten 6.51.0 pulls; not mentioned anywhere). App stays net9.0.
2. `global.json` `{ "test": { "runner": "Microsoft.Testing.Platform" } }` at the sample root.
3. `Tests/MoreSpeakers.Tests.csproj`: same package set as MeetingGroupMonolith (Exe, net10.0, MTP flags, Microsoft.NET.Test.Sdk, xunit.v3 4.0.2, xunit.runner.visualstudio 4.0.1, Bobcat/Bobcat.Xunit/Bobcat.Generators 0.60.4, WolverineFx.Bobcat.Http 6.51.0-alpha.1 (pulls WolverineFx.Bobcat), Alba 9.0.0-rc.2, Shouldly), `EmitCompilerGeneratedFiles`; kept `<Using Include="Xunit" />` (this time on purpose). Assembly name must stay `MoreSpeakers.Tests` (the app's `InternalsVisibleTo`).
4. Same fixture/base as MeetingGroupMonolith: collection fixture, `UseSetting("ConnectionStrings:Marten", …)`, `DisableAllExternalWolverineTransports`, `RunWolverineInSoloMode`, `ApplicationAssembly`; base `MoreSpeakersSpec : WolverineHttpSpec`, `ResetAsync()` per spec (works on a documents-only Marten store, replacing `Advanced.Clean.DeleteAllDocumentsAsync()`); `[assembly: EventModelName("MoreSpeakers")]`, Parallelization None. `DatabaseCollection` deleted (one collection fixture serialises anyway).
5. Seeding via `GivenReadModel<Speaker>(…)` / `GivenReadModel<Mentorship>(…)` replaced every `LightweightSession()` + `SaveChangesAsync()` block.

## Diagnostics, crashes, surprising renders
No build diagnostics, no crashes. Renders:
1. **Blank first row in every multi-value grid**: the command table under a `When` and the `ThenRefusedWithProblem` grid both render an empty row #1 before the real row #2:
   ```
       ✓ When  RegisterSpeaker is posted to "/api/speakers" (1ms)
   │ # │ message         │ values                                                 │
   │ 1 │                 │                                                        │
   │ 2 │ RegisterSpeaker │ Email: "dup@test.com", FirstName: "Second", …          │
       ✓ And   refused with ProblemDetails(Detail: Speaker with email 'dup@test.com' already registered)
   │ # │ Detail                                               │ Status │
   │ 1 │                                                      │   OK   │
   │ 2 │ Speaker with email 'dup@test.com' already registered │   OK   │
   ```
   (MeetingGroupMonolith's shorter commands rendered inline, no grid.) Medium: looks like a phantom row/item.
2. **A `ShouldContain` member check renders the collection's `ToString()`**: `x => x.Expertise.ShouldContain("C#")` → cell `System.Collections.Generic.List`1[System.String]`, and the expected `C#` is not shown. Medium.
3. `UpdateSpeakerProfile`'s `SpeakerId: Speaker` again — `speaker.Id` rendered as the named value `Speaker`, which reads as a type name.
4. `Given the Speaker read model is Speaker` (no values) for `GivenReadModel` — readers can't see what was arranged. And every `ThenDocument` emits `And the Speaker read model has caught up` though Speaker is a plain document.
5. `When  Speaker[] is read from "/api/speakers"`; acceptable.

## Vocabulary used / missing
Base `WolverineHttpSpec`. Used: `GivenReadModel<T>`, `WhenPosted(cmd)`, `WhenPosted<T>(cmd)`, `WhenPut(cmd)` (route `{speakerId}` filled from `SpeakerId`, case-insensitively — nice), `WhenReading<T>(route, values)`, `ThenResponseIs`, `ThenNotFound`, `ThenRefusedWith("Cannot mentor yourself")` (substring of the FluentValidation ProblemDetails body), `ThenRefusedWithProblem(Specify<ProblemDetails>().With(x => x.Detail, …))`, `ThenDocument<T>(id, Specify…)` incl. member checks (`ShouldBe`, `ShouldBeTrue`, `ShouldContain`, `ShouldNotBeNull`), `ThenNoReadModel<T>()`, `ThenMatches`.
Gaps:
- `WhenPut` has no `WhenPut<TResponse>` (POST has `WhenPosted<TResponse>`); I didn't need the body because `ThenDocument` reads the store, but the asymmetry will bite (`TheResponse<T>()` is the workaround).
- "exactly one in the list" again needed a hand step (`TheOnlyItem<T>` with `SpecAssert.Check`), because `Expect.*` collection expectations are not in 0.60.4 (see MeetingGroupMonolith). The original test's `(speakers.Length >= 1).ShouldBeTrue()` has no vocabulary equivalent ("contains a matching item").
- No refusal-plus-unchanged step for documents (`ThenNoEvents` is the event-store half; for a document app I re-read the document with `ThenDocument` to prove the refused accept left it `Active`).

## Event Model and manifest
- Manifest: 10/10 identities as expected (`Speakers/a duplicate email is refused`, `Mentorships/nobody can mentor themselves`, …).
- Slices (model `MoreSpeakers`): `RegisterSpeaker`, `UpdateSpeakerProfile`, `RequestMentorship`, `AcceptMentorship`, `CompleteMentorship` = Command/Http **with no route and no HTTP method** (all by-type acts; `UpdateSpeakerProfile` therefore doesn't even say it is a PUT). `GET /api/speakers/{id}` = Query, route, ReadsFrom `Speaker` (correct). **`GET /api/speakers` (`WhenReading<Speaker[]>`) = View, no trigger, no route** — the array read act is dropped exactly like `List<T>` in MeetingGroupMonolith.
- Because the app is documents-only, no slice has events/aggregates; `GivenReadModel` arranges stamped nothing visible here except ReadModelTypes; document asserts only reach ResolvedTypes.

## ai-skills gaps
- Nothing says companion packages (`Marten.AspNetCore`) must be bumped in lockstep with the Marten that WolverineFx.Marten pulls (restore succeeded either way? I bumped pre-emptively; NU1605 downgrade would be the symptom).
- Skill's `WolverineHttpSpec` table lists `WhenPut` but not that only POST has a typed-response overload.
- Skill has no guidance for document-only (no event sourcing) apps: `GivenReadModel` as "arrange a document", `ThenDocument` as "assert a document", and that the Event Model will show only commands for them.
- (Same as MeetingGroupMonolith: Expect.* not in 0.60.4, `ThenTheResponseStatusIs` doesn't exist, `WhenPosted` route-values example is wrong for WolverineHttpSpec.)

## Candidate issues
- Bobcat: phantom empty row #1 in the command grid under `When` and in `ThenRefusedWithProblem`'s grid.
- Bobcat: member-check cells for `ShouldContain`/collection members render `List`1[System.String]` instead of the items and the expected value.
- Bobcat: array `T` in `WhenReading<T[]>` (and `List<T>`) loses the read act → View slice with no route (same root cause as MeetingGroupMonolith).
- Bobcat: by-type HTTP acts (`WhenPosted/WhenPut(cmd)`) stamp neither route nor verb on the slice.
- WolverineFx.Bobcat (wolverine): `WhenPut<TResponse>` / `WhenPatched<TResponse>` overloads to match `WhenPosted<TResponse>`.
- Bobcat: `GivenReadModel` render should show the document's values (or key members), not `… read model is Speaker`.
- ai-skills: a short "document-only app" section (GivenReadModel/ThenDocument/what the model will show); lockstep-bump note for Marten companion packages.
