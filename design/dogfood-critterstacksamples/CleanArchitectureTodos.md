# CleanArchitectureTodos
- Tests before: 11 (1 file `Tests/TodoListTests.cs`, xUnit v2 + VSTest + Alba 8.5.3 + Shouldly; document store only, int HiLo ids, arrange through POSTs)
- Specs after: 11 in 2 feature classes (`Todo Lists` 7, `Todo Items` 4) on `WolverineHttpSpec`: **11 passing / 0 failing / 0 skipped**, green on first run.

## Adoption steps (what it took)
Same recipe as BankAccountES/BookingMonolith: app WolverineFx.* 6.30.0 -> 6.51.0 (net9.0 kept), `public partial class Program;`, test csproj as before, `global.json`, `TestSupport.cs` (env-var connection string `dogfood_cleanarchitecturetodos`), `[assembly: EventModelName("CleanArchitectureTodos")]` — the app sets **no `opts.ServiceName`**, so the right model name had to be guessed (the skill only says "= opts.ServiceName").

## Diagnostics, crashes, surprising renders
- `CS0308: The non-generic method 'WolverineHttpSpec.WhenPut(object, Action<Scenario>?)' cannot be used with type arguments` — only `WhenPosted` has a `<TResponse>` form; PUT/PATCH/DELETE need `await WhenPut(...); var body = await TheResponse<T>();`. Low (WolverineFx.Bobcat).
- **DELETE with no request body has no act.** `WhenDeleted(object command[, route])` requires a command; `DELETE /api/todolists/{id}` and `DELETE /api/todoitems/{id}` take only a route id. Fell back to `WhenTracked("the todo list is deleted", () => AlbaHost.Scenario(x => { x.Delete.Url($"/api/todolists/{list.Id}"); x.StatusCodeShouldBe(204); }))` — raw Alba, status asserted inside Alba so `ThenResponseIs` is unavailable, and nothing reaches the model (see below). Medium.
- Routes whose id is not on the body (`PUT /api/todolists/{id}` with `UpdateTodoListRequest(Title, Colour)`) can't use the by-type `WhenPut(cmd)`; interpolated `WhenPut(cmd, $"/api/todolists/{original.Id}")` works and the generator resolved it to the template (good).
- `GivenReadModel<TodoList>(new TodoList { … })` with `Id = 0` gets its HiLo id assigned in place, so `original.Id` is usable after the Given — worked, but undocumented (and would not work for a record/immutable doc).
- No BOBCAT diagnostics, no crashes.
- Renders (low): every `ThenDocument<T>` adds a `the TodoList read model has caught up` step, even in an app with no projections at all (noise: it doubles the Then lines); `Given the TodoList read model is TodoList` again shows no values; `Then the Int32 matches ✓ Int32: Int32 ✓ value: 0` for counts; `ThenDocument<TodoList>(list.Id)` renders `there is a TodoList 4` (the HiLo number, fine).

## Vocabulary used / missing
- Used: `GivenReadModel<T>`, `WhenPosted<T>(cmd)`, `WhenPosted(cmd)`, `WhenPut(cmd, $"…")`, `WhenPatched(cmd, $"…")`, `TheResponse<T>()`, `WhenReading<T>(route)`, `WhenTracked` (fallback), `ThenResponseIs`, `ThenRefusedWith("'Duplicate' already exists.")` (matched the ValidateAsync ProblemDetails), `ThenDocument<T>(id[, expected])`, `ThenNoReadModel<T>(id)`, `ThenMatches`.
- Missing: a body-less `WhenDeleted(route)` / `WhenDeleted(route, params values)` (like `WhenReading`); `WhenPut<TResponse>`/`WhenPatched<TResponse>`; collection matching for nested `Items` (did `stored.Items.Single()` + `ThenMatches`; `Expect.*` not in 0.60.4); a "document was stored/deleted" assertion that stamps the model (all slices here are document-only).
- Asserts are stronger than the original's: the original update/patch/delete tests checked only the status code; the specs now read the stored document back.

## Event Model and manifest
- Manifest: all 11 identities as expected (`Todo Lists/create todo list duplicate title returns 400`, `Todo Items/update todo item detail`, …).
- Good: interpolated routes resolved to the endpoint templates with the right verb: `PUT /api/todolists/{id}`, `PUT /api/todoitems/{id}`, `PATCH /api/todoitems/detail/{id}`. `GET /api/todolists` is a Query, `ReadsFrom = [TodoList, TodosVm]`.
- **Wrong: every Command slice has `ReadModelTypes = [TodoList]`** (CreateTodoListRequest, UpdateTodoListRequest, CreateTodoItemRequest, UpdateTodoItemRequest, UpdateTodoItemDetailRequest) — stamped by the `GivenReadModel<TodoList>` arrange of the document the endpoint loads/modifies. Same defect as BookingMonolith.
- **Wrong: the two DELETE slices are `Pattern = View`** (`ReadModelTypes = TodoList`, no trigger): `WhenTracked` stamps nothing by design, so all that is left is the arranged read model, and "only a read model" infers View. A delete command shows up on the canvas as a projection.
- `CreateTodoListRequest` (by-type `WhenPosted`) again has no TriggerOrigin, while the interpolated-route ones do.

## ai-skills gaps
- `[assembly: EventModelName("<opts.ServiceName>")]` — no guidance for an app that never sets `ServiceName`.
- No guidance for a body-less DELETE (or any verb without a request type); `WhenDeleted(command, …)` is the only form documented.
- Doesn't mention the act table lacks `WhenPut<TResponse>` and that `TheResponse<T>()` is the way.
- Doesn't say `WhenTracked` + an arranged read model infers a **View** slice (only "stamps nothing on the Event Model"), so an outside agent using the documented escape hatch silently miscolours the model.
- The skill's main guidance is event-sourced; nothing on document-only (CRUD) apps — what slice a document write is, and that arranging documents marks them as read models.

## Candidate issues
- WolverineFx.Bobcat (wolverine): `WhenDeleted(string route, params object[] routeValues)` (no body), attributed `[EventModelTrigger(Http, Route = nameof(route), HttpMethod = "DELETE")]`.
- WolverineFx.Bobcat (wolverine): `WhenPut<TResponse>` / `WhenPatched<TResponse>` for symmetry with `WhenPosted<TResponse>`.
- Bobcat (generator): don't infer `View` from arranged read models alone when the test has an act (even an unstamped `WhenTracked`); leave Pattern null rather than wrong.
- Bobcat (generator): arranged read models on a Command slice -> `ReadsFrom`, not `ReadModelTypes` (repeat from BookingMonolith).
- Bobcat: skip the "read model has caught up" step (or fold it into the read) when the store has no async projections.
- ai-skills: document-only (CRUD) recipe: GivenReadModel + by-route acts + ThenDocument, and the model consequences above.
