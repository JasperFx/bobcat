# EcommerceMicroservices
- Tests before: 14 (1 file `CatalogTests.cs`, xUnit v2 + VSTest + Alba 8.5.3 + Shouldly) — **only the Catalog service is tested**;
  Basket, Discount and Ordering (the RabbitMQ-connected services) have no tests at all.
- Specs after: 14 on `WolverineHttpSpec` against the Catalog host: **14 passing / 0 failing / 0 skipped**, green first run.
- App: document-only CRUD (`Product` with a Guid id and a `List<string> Category`), FluentValidation, `[Entity(Required = true)]`, seed data at startup.

## Adoption steps (what it took)
Same recipe (WolverineFx.* 6.30.0 -> 6.51.0 in all five services, app net9.0; xunit.v3 + MTP; Bobcat* 0.60.4;
WolverineFx.Bobcat.Http 6.51.0-alpha.1; Alba 9.0.0-rc.2; `<Using Include="Xunit" />`; global.json; fixture + abstract
`CatalogSpec : WolverineHttpSpec` with `ResetAsync()`; `EventModelName("Catalog")` = `opts.ServiceName`; serial). Catalog references
no broker; `DisableAllExternalWolverineTransports` is there for the shape. Keep `AssemblyName=Tests` (InternalsVisibleTo).
A multi-service solution raises a question the skill doesn't answer: one spec project per service (each with its own `EventModelName`
= that service's name) or one project hosting several Alba hosts? Only one model name per assembly is possible, so it has to be one per service.

## Diagnostics, crashes, surprising renders
- **A `List<string>` member renders as its CLR type name in the object grid** (medium — it is the cell a reader came to read):
  ```
  │ # │ Name    │ Category                       │ Description  │ Price │ Status │
  │ 1 │ Updated │ System.Collections.Generic.Lis │ Updated desc │ 250   │   OK   │
  │   │         │ t`1[System.String]             │              │       │        │
  ```
  Both for `.With(x => x.Category, new List<string>{…})` and for a member check `x => x.Category.ShouldContain("Smart Phone")`.
  (Bobcat's CLAUDE.md says `ObjectComparison.Format` now prints a collection's items — #456, after 0.60.4 — so likely fixed on main; verify.)
- The empty row 1 in the multi-line command table again (`WhenPut(new UpdateProduct(…))`).
- `Id: Product` for `p.Id` taken from an arranged object; `When <>f__AnonymousType0 is sent as a DELETE to …`.
- No BOBCAT diagnostics; `WhenDeleted(new { }, $"/products/{id}")` works (route-parameter DELETE, unlike CqrsMinimalApi's `[FromQuery]`).

## Vocabulary used / missing
- Used: `GivenReadModel<T>`, `WhenPosted<T>(cmd)`, `WhenPosted(cmd)`, `WhenPut(cmd)` (by type — route `/products` has no parameters),
  `WhenReading<T>(route, values)`, `WhenReading<List<T>>`, `WhenDeleted(new { }, route)`, `ThenResponseIs`, `ThenNotFound`, `ThenRefusedWith`,
  `ThenDocument<T>`, `ThenNoReadModel<T>(id)`, `ThenMatches` with member checks (`Specify<Product>(x => x.Name.ShouldBe(…), …)`).
- Missing: collection expectations (`Expect.InAnyOrder`/`Containing` not in 0.60.4 — projected names through `Select(...).ToArray()`),
  body-less DELETE, `WhenPut<TResponse>` (the original read the PUT's response).

## Event Model and manifest
- Manifest: all 14 identities.
- `CreateProduct` / `UpdateProduct`: Command, Http, no route (by-type acts; trend). Get-by-id: feature-named slice `Catalog`, Query, GET `/products/{id}`.
- **Explicitly binding the list GET does not rescue it**: `[BobcatSpec(typeof(Product), Pattern = "Query")]` on the `WhenReading<List<Product>>`
  spec gives slice `Product`, Pattern Query, but **no TriggerKind, no route, no ReadsFrom** — the list read act is still unrecognised, so the
  declared pattern is the only thing on the slice (and the store's projection-derived `Product` View slice would merge with it by name).
- The by-category list and both DELETE specs are on no slice (no roles), silently.

## ai-skills gaps
- One spec project per service in a microservice solution (one `EventModelName` per assembly); nothing on it today.
- Nothing else beyond the shared list.

## Candidate issues
- Bobcat (rendering): collection members in property grids print the CLR type name in 0.60.4 — confirm fixed on main by #456 and ship it.
- Bobcat (generator): list read acts (trend) — an explicit `[BobcatSpec(typeof(T), Pattern = "Query")]` still loses the route.
