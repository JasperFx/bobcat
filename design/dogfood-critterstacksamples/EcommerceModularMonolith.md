# EcommerceModularMonolith
- Tests before: 17 (1 file `EcommerceTests.cs`, xUnit v2.9.2/VSTest, Alba 8.5.3, Shouldly; one `IAsyncLifetime` host per test)
- Specs after: 20 projected specs on `WolverineHttpSpec` (17 ported, `Can_store_and_get_basket` split into store + get, plus 2 added refusals: FluentValidation 400 and the checkout `ProblemDetails` 404). **total 20, failed 0, succeeded 20, skipped 0.**
- App: document-only Marten in 4 module schemas, Wolverine.HTTP, FluentValidation, a cascaded `BasketCheckoutEvent` to a durable local queue whose handler stores an `Order`. Bumped WolverineFx.* 6.30.0 → 6.51.0; **app stayed on net9.0** (test project net10.0 referencing it builds and runs fine).

## Adoption steps (what it took)
Same recipe as CqrsMinimalApi, which now worked first time: skill csproj **minus** the direct `Alba` ref (NU1605 against WolverineFx.Bobcat.Http's Alba 9.0.0-rc.2) **plus** `<Using Include="Xunit" />`; `global.json` MTP runner; skill's `AppFixture`/collection/`EcommerceSpec : WolverineHttpSpec` with `ResetAsync()`; `[assembly: EventModelName("EcommerceMonolith")]` (= `opts.ServiceName`); connection string through a `[ModuleInitializer]` setting `ConnectionStrings__Marten` (avoids Alba `UseSetting` → command-line args). No BOBCATnnn diagnostics at all (checked with `-p:ErrorLog=…sarif`: zero BOBCAT entries, info included).
Durable local queues + `DisableAllExternalWolverineTransports` + `RunWolverineInSoloMode` were fine: the tracked session waited for the cascaded `BasketCheckoutEvent` handler, so the `Order` existed when the Then ran.

## Diagnostics, crashes, surprising renders
- `ThenSingleDocument<Order>(…)` after the checkout act → **error**, ends the test: `Bobcat.Engine.SpecCriticalException : ThenSingleDocument<Order>() reads the stream the scenario is about, but the act started none and the givens arranged none. Name it: ThenReadModel<Order>(id, ...).` The name reads as "the single Order document"; it is actually stream-bound. A document created by a cascaded handler has an id nobody in the spec knows, so there is no step for it (workaround below). Severity: medium (vocabulary gap + misleading name).
- `WhenDeleted(new { product.Id }, $"/products/{id}")` **worked** here (204) although it sends a JSON body — so CqrsMinimalApi's 404 is specific to a `[FromQuery] int id` DELETE endpoint, not to every body-less DELETE.
- **Empty first row in act tables**, again, for every `WhenPosted(command)` whose command is too long to render inline, and in `ThenRefusedWithProblem(Specify<ProblemDetails>()…)`:
  ```
  ✓ When  CheckoutBasket is posted to "/basket/checkout" (23ms)
  │ # │ message        │ values                                         │
  │ 1 │                │                                                │
  │ 2 │ CheckoutBasket │ UserName: "checkoutuser", CustomerId: customer, …
  ✓ And   refused with ProblemDetails(Detail: Basket not found for user 'nobody') (1ms)
  │ # │ Detail                             │ Status │
  │ 1 │                                    │   OK   │
  │ 2 │ Basket not found for user 'nobody' │   OK   │
  ```
- **Guid values render as names**: `Product(Id: Product, …)`, `Product(Id: Product2, …)`, `When <>f__AnonymousType1<Guid>(Id: Product) is sent as a DELETE …`, `✓ And there is no Product Product`. In the checkout spec `CustomerId: customer` (my local's name) and, for `Guid.NewGuid()` inline, `CustomerId: Customer`. It is presumably Bobcat's id aliasing, but "there is no Product Product" is unreadable and a reader cannot tell it is a Guid.
- Generic and anonymous type names leak raw: `When List`1 is read from "/products"`, `When <>f__AnonymousType0<String>(UserName: "deleteuser") is sent as a DELETE to …`.
- `Given the ShoppingCart read model is ShoppingCart` — a document with a nested collection renders as just its type name, no values.
- `ThenRefusedWithProblem()` with no expectation renders only `✓ status: 4xx` — the FluentValidation errors are not shown anywhere on a pass.
- Passing specs render only when the **test exe** is run directly with `BOBCAT_SPEC_CONSOLE=1`; under `dotnet test --project Tests` the same variable prints nothing (MTP captures the child's stdout). An agent will use `dotnet test` first.

## Vocabulary used / missing
Used: `WhenPosted(cmd)` / `WhenPosted<T>(cmd)` by type, `WhenPut(cmd)` by type (route `/products`, `/discounts` found from the request type), `WhenDeleted(obj, route)`, `WhenReading<T>(template, values)`, `GivenReadModel<T>`, `ThenDocument<T>(id, Specify…)` (Guid and **string** ids — `ThenDocument<ShoppingCart>("testuser", …)` works), `ThenNoReadModel<T>(id)` / `ThenNoReadModel<T>()`, `ThenResponseIs`, `ThenNotFound`, `ThenRefusedWithProblem(Specify<ProblemDetails>().With(x => x.Detail, …))`, `ThenMessageSent(Specify<BasketCheckoutEvent>()…)`, `ThenNoMessageSent<T>()`, `ThenMatches`. Partial objects over computed members (`TotalPrice`) work.
Gaps:
- **No step for "the one document of type T the act produced"** (id minted by a downstream handler). Wrote:
  ```csharp
  await using var query = AlbaHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
  var orders = await query.Query<Order>().ToListAsync();
  ThenMatches(orders.Single(), Specify<Order>().With(x => x.CustomerId, customer).With(x => x.Status, OrderStatus.Pending));
  ```
  (Marten-specific, and `orders.Single()` throwing is an error rather than a gathered wrong.)
- **Body-less DELETE**: every `WhenDeleted` needs a command object; for `[WolverineDelete("/products/{id}")] Delete(Guid id, [Entity] Product)` there is none, so I passed an anonymous `new { product.Id }` — which renders as `<>f__AnonymousType1<Guid>` and stamps nothing on the model (below).
- `Expect.Exactly/InAnyOrder` not in 0.60.4 (see CqrsMinimalApi), so list results were asserted as `ThenMatches(names.OrderBy(x => x).ToArray(), new[] {…})`.
- `GivenReadModel<T>(object)` needs the explicit `<T>` every time (CS0411 otherwise).

## Event Model and manifest
- Manifest: 20 specs, identities `{Feature}/{scenario}` exactly as expected (`Basket/checking out a basket deletes it and hands ordering an order`, …).
- Model `EcommerceMonolith`, Domain stamped on every slice. Per slice:
  - Command slices right: `StoreBasket`, `CheckoutBasket` (+ `PublishedMessages` BasketCheckoutEvent — good), `CreateProduct`, `UpdateProduct`, `CreateCoupon`, `UpdateCoupon`, `CreateOrder`. **But none has a route**: by-type `WhenPosted(cmd)`/`WhenPut(cmd)` leave `TriggerOrigin` empty, and no BOBCAT038 is reported. The generator already indexes `[WolverinePost]`/`[WolverinePut]` endpoints; matching the endpoint whose request parameter type is the command would give the route at compile time.
  - Query slices right: `GET /basket/{userName}`, `GET /products/{id}`, `GET /discounts/{productName}`, `GET /orders/{id}` (ReadsFrom Order + OrderDto).
  - **Wrong: every list GET is `View` with no trigger** — `GET /products`, `GET /products/category/{category}`, `GET /orders` (`WhenReading<List<T>>`). Same collection-type-argument bug as CqrsMinimalApi.
  - **Wrong: every DELETE slice is `View` with no trigger** — `DELETE /products/{id}`, `/basket/{userName}`, `/discounts/{id}`, `/orders/{id}`. The anonymous command stamps no Command role, so the act is not recognised at all (its HTTP trigger and route are dropped too), and the `GivenReadModel` read-model role makes it a View. A delete is a command.
  - The downstream `BasketCheckoutEventHandler` (creates the Order) is not on the model — expected, no spec acts on it.

## ai-skills gaps
- `ThenSingleReadModel`/`ThenSingleDocument` are described as "the T of the stream the scenario is about", accurate but easy to misread; the skill needs an explicit "for a document whose id you don't know there is no step — query it" note, or the vocabulary needs the step.
- No guidance for document-only (non-event-sourced) Wolverine.HTTP apps at all, though `GivenReadModel` / `ThenDocument` / `ThenNoReadModel` / `ThenMessageSent` cover most of them; the skill's whole framing is event streams.
- No guidance for DELETE endpoints without a request type, nor that by-type acts leave the slice's route empty.
- Running section: say `dotnet test` swallows the rendered tree even with `BOBCAT_SPEC_CONSOLE=1`; run the test exe.
- Same as CqrsMinimalApi: `Expect.Exactly` & co not in 0.60.4; csproj template (Alba, `Using Xunit`).

## Candidate issues
- Bobcat: list read acts (`WhenReading<List<T>>`, `T[]`, `IReadOnlyList<T>`) are not read acts → list Query slices become View with no trigger (repeat of CqrsMinimalApi; affects 3 more slices here).
- Bobcat / WolverineFx.Bobcat (wolverine): a DELETE act with no knowable command type drops its trigger and route; add a body-less `WhenDeleted(route, params routeValues)` carrying `[EventModelTrigger(Http, Route, "DELETE")]`, and let the generator treat an HTTP write act with no Command role as a Command slice.
- Bobcat: resolve the route of a by-type `WhenPosted/WhenPut(cmd)` at compile time from the endpoint whose request type matches (or report BOBCAT038 — today it is silent).
- Bobcat: empty first row in act/ProblemDetails tables (`│ 1 │ │ │`).
- Bobcat: anonymous/generic type names in step text (`<>f__AnonymousType1<Guid>`, `List`1`).
- Bobcat: Guid aliasing renders "there is no Product Product" and `CustomerId: customer`; make aliases distinguishable from values (e.g. `<Product#1>`).
- WolverineFx.Bobcat (wolverine) / Bobcat: a step for a single document of `T` created by the act (`ThenTheOnly<T>(expected)` / `ThenDocuments<T>(Expect…)`) without knowing its id; rename or document `ThenSingleDocument`'s stream-bound meaning.
- ai-skills: document-only app guidance; DELETE guidance; `dotnet test` vs exe rendering.
