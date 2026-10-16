# ContributorApi
- Tests before: 11 (1 file `ContributorTests.cs`, xUnit v2 + VSTest + Alba 8.5.3 + Shouldly; every arrange through a POST)
- Specs after: 11 on `WolverineHttpSpec`: **11 passing / 0 failing / 0 skipped**, green on the first run.
- App: document-only CRUD (Marten `Contributor` with an int HiLo id), Wolverine.HTTP + FluentValidation, `[Entity(Required = true)]` lookups.
- No in-repo port to compare against.

## Adoption steps (what it took)
Same recipe as OutboxDemo/PaymentsMonolith, about five minutes of copying: WolverineFx.* 6.30.0 -> 6.51.0 (app net9.0), xunit.v3 + MTP,
Bobcat* 0.60.4, WolverineFx.Bobcat.Http 6.51.0-alpha.1, Alba 9.0.0-rc.2, `<Using Include="Xunit" />`, global.json, fixture +
collection + abstract `ContributorSpec : WolverineHttpSpec` with `ResetAsync()`, `EventModelName("ContributorApi")`, serial.
The app has `InternalsVisibleTo Include="Tests"`, so the test assembly must keep the name `Tests`.

## Diagnostics, crashes, surprising renders
- None blocking; no BOBCAT diagnostics.
- **`WhenDeleted(new { }, $"/api/contributors/{id}")` works here** (204/404 as expected) — the 404 CqrsMinimalApi hit is specific to a
  `[FromQuery]` DELETE that receives a JSON body. Still renders `When <>f__AnonymousType0 is sent as a DELETE to "/api/contributors/3"`,
  because there is no body-less DELETE act and the anonymous type's CLR name is the sentence.
- `SpecAssert.Fact(updated.UpdatedAt is not null, "UpdatedAt is stamped")` in the test body after `ThenDocument` — silent on pass, as elsewhere.
- `ThenMatches(names, new[] {…})` renders `the String[] matches`; `GivenReadModel` renders no values; GET by id shows the template
  (`"/api/contributors/{id}"`) while PUT/DELETE show the filled route.

## Vocabulary used / missing
- Used: `GivenReadModel<T>`, `WhenPosted<T>(cmd)`, `WhenPosted(cmd)`, `WhenPut(cmd, route)`, `WhenDeleted(new { }, route)`,
  `WhenReading<T>(route, values)`, `WhenReading<T[]>`, `ThenResponseIs`, `ThenNotFound`, `ThenRefusedWith`, `ThenDocument<T>(id, …)`,
  `ThenNoReadModel<T>(id)`, `ThenMatches`, `[BobcatSpec(Name = "…")]`.
- Missing: `WhenPut<TResponse>` (only `WhenPosted` has a response-returning form; the original test read the PUT's response body,
  the spec has to re-read the document instead); a body-less `WhenDeleted(route)`; an ordered-collection expectation (`Expect.Exactly` is
  not in 0.60.4).

## Event Model and manifest
- Manifest: all 11 identities as expected, including the explicit `Name =` one.
- `CreateContributor`: Command, Http, no route (by-type `WhenPosted`, trend). `UpdateContributor`: Command, PUT `/api/contributors/{contributorId}`
  — resolved from the interpolated route against the endpoint template. Correct.
- **Specs with a bare `[BobcatSpec]` and a read act land on a slice named after the FEATURE**: the two get-by-id specs became slice
  `Contributors` (Query, GET `/api/contributors/{id}`) — documented behaviour, but a slice named after the test class is not a name anyone
  would draw; `GET /api/contributors/{id}` (what CqrsMinimalApi got when bound by route) would have been better.
- **The list GET (`WhenReading<Contributor[]>`) and both DELETE specs contribute nothing to the model** — no role on `Contributor[]`
  (the list-read trend) and no command type on a DELETE — so three specifications are silently absent from the Event Model, with no
  diagnostic saying so.

## ai-skills gaps
- Nothing new beyond the shared list; worth saying explicitly that a spec with no roles is not on the model at all.

## Candidate issues
- WolverineFx.Bobcat: `WhenPut<TResponse>` / `WhenPatched<TResponse>` / `WhenDeleted<TResponse>` (parity with `WhenPosted<TResponse>`).
- Bobcat: a `[BobcatSpec]` that ends up on no slice (no roles, no binding) could get an info diagnostic, so "not on the model" is visible.
