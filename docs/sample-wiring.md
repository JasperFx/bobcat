# Sample Wiring Playbook

How to wire a sample host to `BobcatRunner` so its `.feature` specs run end-to-end through
Alba. This is the canonical reference for issue #8; the reference implementation is
`samples/CqrsMinimalApi/Tests/`.

## Before anything: start the database

```bash
cd samples && docker compose up -d
```

Published on **5433**, which is the port every sample's default connection string already names,
with one database per sample created by `samples/init/create-databases.sql`. This did not exist
until `PaymentsMonolith` was wired, and its absence is not a footnote — the whole lesson of this
playbook is that **a sample is not fixed until it has been run**, and there was nothing to run it
against. Wiring a sample without starting this is wiring it blind.

**5433 collides with the Wolverine repo's own Postgres**, which publishes on the same port and is
routinely left running for days. `docker compose up -d` then fails with `Bind for 0.0.0.0:5433
failed: port is already allocated`, and killing the other repo's container to get your own is the
wrong trade. Bobcat's *root* `docker-compose.yml` already learned this and sits on 5445 precisely
so it never collides. The fastest way through, since it is the same image and the same
`postgres`/`postgres` credentials, is to create the sample databases in whichever instance holds
the port:

```bash
for db in bank_account booking clean_architecture_todos cqrs_minimal_api ecommerce \
          inflow meeting_groups more_speakers outbox_demo; do
  docker exec <container> psql -U postgres -c "CREATE DATABASE $db"
done
```

Moving `samples/docker-compose.yml` off 5433 is the durable fix, but it means editing the
connection string in all eleven `appsettings.json` files, so it is a decision rather than a
detail.

## The playbook

For each sample, replicate what `CqrsMinimalApi` has:

1. **Add a `Tests/` subdirectory** with three files:
   - `Tests.csproj` — `net10.0`, `OutputType=Exe`; project-references the host + `Bobcat` +
     `Bobcat.Alba` + `Bobcat.Generators` (as an analyzer); `<Compile Include="..\<Project>Fixture.cs" />`
     to link the fixture in; `<AssemblyName><Project>.Tests</AssemblyName>` to match
     `[InternalsVisibleTo]`.
   - `SpecsRunner.cs` — an **explicit `static class SpecsRunner` with a `Main`**. Do **not** use
     top-level statements (see footgun #1).
   - `AssemblyAttributes.cs` —
     `[assembly: WebApplicationFactoryContentRoot("<HostAssemblyName>", "../../../..", "appsettings.json", "1")]`
     so Alba can find the host's content root despite the nested layout (footgun #2).
2. **Update the host `.csproj`:** (target the canonical [version matrix](versions.md) — `net10.0`,
   `WolverineFx.* 6.5.1`, `Marten 9.6.0`; the move off `5.30.0`/`net9.0` is a major upgrade)
   - Bump `TargetFramework` to `net10.0` if still on `net9.0` (footgun #1 also presents as a TFM mismatch).
   - `<InternalsVisibleTo Include="<Project>.Tests" />`.
   - Exclude the fixture from the host compile group: `<Compile Remove="<Project>Fixture.cs" />`.
3. Make the fixture extend `Bobcat.Fixture` and use `Context!` (not a stored field), or take an
   `IStepContext` parameter per step. **`Fixture` is not optional** — the generator's fixture
   discovery is `inheritsFrom(symbol, "Bobcat.Fixture")`, so a class that merely carries
   `[FixtureTitle]` matches nothing and the feature generates no code at all. The symptom is
   silence, not an error: the project compiles, and `list` reports no features.
4. **Give the resource a reset hook if the host has persistent state.** `AlbaResource`'s `reset:`
   parameter is `ResetBetweenScenarios`. A suite that passes once per database and then reports
   conflicts for records it believes are new is worse than no suite — and it is the default
   outcome for any sample with a unique index. `samples/OutboxDemo/Tests/SpecsRunner.cs` is the
   worked example (`store.Advanced.Clean.DeleteAllDocumentsAsync()`).
5. **Bring the host API and the fixture into agreement.** This is where the work usually goes —
   fixtures often describe a clean RESTful contract while host endpoints are RPC-style. Refactor
   the host (Path A) rather than weakening the spec. Expect the fixture to describe endpoints
   that do not exist at all: `OutboxDemo`'s posted to `/api/meetings/member-joined` while the host
   exposed one `POST /registration`. Nothing had ever compiled it, so nothing reported the drift.
6. Drop and recreate the host's Marten schema before the first run (old shape may conflict).
7. **Wait for cascaded messages before asserting** if the host routes integration events between
   modules. See footgun 7 — this is the difference between a suite that passes and one that
   passes *reliably*, and it does not announce itself.
8. **Run it twice, then break it once.** Twice, because persistent state is what a first run
   cannot reveal. Broken once, because a spec that cannot go red has told you nothing: change an
   expected value, confirm the failure lands on the step you expected, change it back.
   `PaymentsMonolith` was verified this way, including removing the cascade tracking to confirm
   three scenarios really do fail without it.

## Footguns

### 1. `Program` symbol collision when SpecsRunner uses top-level statements
When the Tests project uses top-level statements **and** project-references the host (which also
does), both compilations synthesize a `Program` in the global namespace. `AlbaResource<Program>`
then binds to the test-runner stub and Alba bootstraps an empty `WebApplication`, crashing
natively in `WebApplication.CreateBuilder` with **`PAL_SEHException` and no managed stack**.

- **Fix:** make `SpecsRunner.cs` an explicit `static class SpecsRunner { static Task Main(...) }`.
- **Diagnostic:** `BobcatRunner.Run` now detects two global-namespace `Program` types and throws a
  clear `BobcatConfigurationException` pointing here, before Alba can crash natively.

### 2. `WebApplicationFactoryContentRoot` requirement when Tests is nested in the host
`samples/.../Tests/` is a **nested** subdirectory of the host. Without
`[WebApplicationFactoryContentRoot]`, ASP.NET Core's `WebApplicationFactory` walks up from the
test bin dir, hits the host `csproj`, and tries to resolve a doubled `<Host>/<Host>/` path —
`DirectoryNotFoundException`. Sibling layouts work without it; nested ones don't.

- **Fix:** add the `[assembly: WebApplicationFactoryContentRoot(...)]` attribute shown above.

### 3. `(body, IResult)` tuple returns silently misrouted by Wolverine.HTTP
Wolverine.HTTP treats tuple returns as `(http-body, ...cascaded-messages)`. Returning
`(CreateStudentResponse, IResult)` cascades the `IResult` as a message with no handler, so the
endpoint returns the wrong status and logs `No routes can be determined for Envelope ...
HttpResults.Created<T>`.

- **Fix:** return `TypedResults.Created<T>(...)` directly instead of a `(body, IResult)` tuple.
- When the endpoint *also* cascades a message, the `IResult` goes **first**:
  `(Created<UserAccount>, UserCreated)`. The first tuple item is the HTTP response under
  Wolverine.HTTP's rules (an `IResult` is executed as-is), and everything after it is a cascaded
  message. `BookingMonolith` does this on all four of its creates.
- The rule is positional, and the other direction works: the **first** element is the response
  and may itself be an `IResult`, everything after it is cascaded. `EcommerceModularMonolith`'s
  checkout returns `(Accepted, BasketCheckoutEvent)` — a 202 with a Location, plus the event the
  Ordering module handles. The original `(bool, BasketCheckoutEvent)` "worked" too, in that it
  cascaded — it just answered every checkout with `true` and a 200.
- **When the endpoint also cascades a message**, it has to return a tuple, so `TypedResults`
  cannot be the whole return value either. Derive a record from `Wolverine.Http.CreationResponse`
  and put it in the first slot — `(ProposalCreation, MeetingGroupProposalAcceptedEvent)` — which
  gives the 201 and the `Location` header and still cascades the second element.
  `samples/MeetingGroupMonolith/Payments/CreateSubscription.cs` is the worked example. The body
  is then `{ id, url }` rather than the entity, so the fixture reads the created record back over
  a GET, which is the assertion the spec wanted anyway.
- This one is upstream in Wolverine, not Bobcat.

### 4. Wolverine 6 no longer ships the runtime compiler
A host left in the default `TypeLoadMode.Dynamic` now fails to **start** with "no
`IAssemblyGenerator` (Roslyn) is registered" (Wolverine GH-2876) — the runtime compiler moved out
of core. Every sample carried over from WolverineFx 5.x has this.

- **Fix:** `<PackageReference Include="WolverineFx.RuntimeCompilation" />` (it auto-registers), or
  `opts.UseRuntimeCompilation()`, or pre-generate with `codegen write` + `TypeLoadMode.Static`.
- A build-only CI job cannot catch this. It takes running the specs, which is the argument for
  doing at least one sample end-to-end rather than declaring a sample fixed when it compiles.

### 5. One step text per attribute
`[Given("a registration for X")]` and `[When("I submit a registration for X")]` stacked on the
same method does not bind both texts — the unbound one fails the build with `BOBCAT002`. Give
each text its own method (they can both delegate to one private helper). This is the generator
working as designed; it is only surprising because the failure names the step, not the method.

### 6. Alba's default 200 assertion
Alba's default `Scenario(...)` asserts a 200 status. The `Bobcat.Alba` helpers call
`s.IgnoreStatusCode()` for you and surface the real status on `HttpResult`, but a sample that
reaches into Alba directly will trip on non-200 paths (201/204/404).

### 7. Durable local queues make the HTTP response an unreliable moment to assert
A modular monolith that routes integration events between modules over `UseDurableInbox()` local
queues returns from the HTTP call **before** the receiving module has handled the cascade. In
`PaymentsMonolith`, `POST /api/users` returns 200 before the Customers module has created the
customer stub, and completing a profile returns before the Wallets module has created the wallet.
Asserting off the response races the handler.

- **Fix:** wrap the call in Wolverine's tracking so the step waits for all message activity:
  ```csharp
  Func<IMessageContext, Task> act = async _ => { captured = await call(); };
  await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30)).ExecuteAndWaitAsync(act);
  ```
  `ExecuteAndWaitAsync` overloads on `Task` and `ValueTask`, and an async lambda converts to both
  — hence the explicitly typed delegate rather than an inline lambda (CS0121).
- `samples/PaymentsMonolith/PaymentsMonolithFixture.cs` (`awaitingCascades`) is the worked
  example. Removing it fails 3 of 11 scenarios, so it is load-bearing rather than defensive.
- This is the seam `Bobcat.CritterStack` owns (`InvokeMessageAndWaitAsync`,
  `ExecuteAggregateCommandAsync<T>`, `WaitForNonStaleProjectionsAsync`); `PaymentsMonolith`
  predates it and still reaches for `Wolverine.Tracking` directly. `BankAccountES/Tests` is the
  sample that uses the package — its reset hook is `host.ResetEventStoresAsync()`, through
  `JasperFx.Events.IEventStore`, with no `using Marten` in the spec project.
- **Do not read a green run as proof the race is absent.** `BookingMonolith` has the same
  shape (registering a user makes the Passenger module store a stub off a durable local queue)
  and passed **10 of 10** runs with the tracking removed, because a one-document handler
  usually beats the follow-up GET. Usually. Keep the tracking wherever the cascade exists, and
  record the measurement either way so the next reader knows which kind of suite they have.
- This is the seam `Bobcat.CritterStack` will eventually own; until that package exists, the
  fixture reaches for `Wolverine.Tracking` directly.

### 8. Marten projection subclasses must be `partial`, and `CreateEvent<T>` is gone
Two separate breakages in the same file, both from the Marten 9 / JasperFx.Events 2 move, and
only the first one is a compile error:

- **`CreateEvent<T>(e => …)` in a projection constructor no longer exists.** The supported form
  is the `Create` method convention: `public static TDoc Create(TEvent e) => …`.
- **A projection subclass that uses convention methods must be declared `partial`.** Marten
  dispatches `Create`/`Apply`/`ShouldDelete` through a compile-time source generator with **no
  runtime fallback**, and it emits into that class. Without `partial` the project compiles clean
  and the host fails to **start** with `InvalidProjectionException: No source-generated dispatcher
  found for …`. A self-aggregating type registered via `Snapshot<T>` does *not* need it — only a
  projection subclass does, which is why a sample can have several aggregates working and one
  projection that kills the host.

This is footgun 4's lesson a second time: a build-only CI job cannot catch either the start
failure or the drifted assertions behind it. `BankAccountES` compiled clean and could not start.

A smaller one from the same move, and this one *is* a compile error: `SnapshotLifecycle` now lives
in `JasperFx.Events.Projections`, not `Marten.Events.Projections`. Every sample of this vintage
that calls `opts.Projections.Snapshot<T>(SnapshotLifecycle.Inline)` needs the extra `using`.

### 9. Expect read endpoints to be missing entirely
Drifted fixtures describe *writes* that were at least plausible, but the assertion side often has
nothing to call. `PaymentsMonolith` had no `GET /api/customers/{id}` at all — the module could
only be written to, so the sample's central claim (registering a user creates a customer stub)
was unobservable. Path A applies: add the endpoint to the host rather than dropping the
assertion. It is usually four lines.

### 10. `HttpResult.Body` is non-null on a 400, and it is not the thing you asked for
The `Bobcat.Alba` helpers deserialize whatever came back into `TResponse` and swallow the
failure. System.Text.Json ignores unknown properties, so a `ProblemDetails` body reads into a
`TodoList` or `TodoItem` without complaint — every property at its default. If the response type
initialises an id (`public Guid Id { get; set; } = Guid.NewGuid();`), the fixture now holds a
perfectly plausible id for a resource that was never created, and the next step 404s somewhere
far from the cause. `CleanArchitectureTodos` hit this when a duplicate-title 400 handed the
fixture a phantom list.

- **Fix:** gate on the status before taking anything from the body —
  `if (result.StatusCode is >= 200 and < 300 && result.Body is not null)`. A `Given` that does
  not assert its own status is exactly where this hides, because nothing reports the 400.

### 11. Program.cs seed data runs under Alba, and the reset hook is what removes it
Several samples seed a few documents from `Program.cs` after `builder.Build()` and before
`app.RunAsync()`. It is tempting to assume that code never executes under Alba, on the theory that
`WebApplicationFactory` intercepts `Build()` and stops the entry point there. It does not:
`EcommerceModularMonolith`'s Marten log shows the `catalog` and `discount` tables being created
**before** `Application started`, which is its `SeedData` querying them. The seed is in the database
when the first scenario begins.

- Consequence: with no reset hook, an assertion like *at least 1 catalog product is returned* is
  satisfied by the seed, not by anything the scenario did. The spec goes green without testing
  the endpoint it names. With the reset hook, every scenario begins from empty *including* the
  seed — so a spec must never count on seeded rows either.
- **Fix:** decide which one you want and say so in `SpecsRunner.cs`. The playbook's default is the
  reset hook (step 4), so scenarios create what they assert on. Write the comment from the log,
  not from memory — the assumption is exactly the kind a comment preserves and a run disproves.

### 12. A cascade that mints its own id is unobservable to the caller
In a modular monolith the interesting write is usually the *second* one — the record another
module creates in response to the first. If that handler does `Id = Guid.NewGuid()`, nobody
outside the process can address what it made: `MeetingGroupMonolith`'s accepted proposal created
a `MeetingGroup` under a fresh Guid, so "accepting the proposal creates the group" could only be
checked by searching the whole list for a matching name. Path A applies: give the created record
the id the caller already holds (the group takes the proposal's id, which is what the original
project did too), and footgun 9's read endpoint then has something to read. Same shape in the
Payments direction — a subscription's cascade is only observable because the `Member` it updates
carries the user's id.
