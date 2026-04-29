# Bobcat Development Context — April 12, 2026

## What Was Built Today

### Bobcat.Alba (PR #4, merged to main)
- `AlbaResource` — non-generic, factory-based (`Func<Task<IAlbaHost>>`), implements `IHostResource`
- `AlbaResource<TProgram>` — generic, uses `AlbaHost.For<TProgram>()`, implements `IHostResource`
- Both expose `IAlbaHost AlbaHost` for `Scenario()` calls and `IHost Host` for service access
- `AlbaStepContextExtensions` — `ScenarioAsync`, `PostJsonAsync`, `GetJsonAsync`, `PutJsonAsync`, `DeleteAsync`, `LastScenarioResult`
- `AlbaAuthExtensions` — `ScenarioWithClaimsAsync` for per-scenario auth overrides (uses `WithClaim` singular, not `WithClaims`)
- 14 tests passing in `Bobcat.Alba.Tests`

### Bobcat.Wolverine (PR #4, merged to main)
- **No `WolverineResource`** — deliberately eliminated. Users bring their own `IHost`.
- Pure extension methods on `IStepContext` that resolve `IHost` via `IHostResource`
- `WolverineStepContextExtensions` — `InvokeMessageAndWaitAsync`, `SendMessageAndWaitAsync`, `ExecuteAndWaitAsync`, `TrackActivity`
- Works against ANY `IHostResource` (HostResource, AlbaResource, or custom)
- Wolverine tracking API is in `Wolverine.Tracking` namespace, main `WolverineFx` package

### Bobcat Core Additions (PR #4, merged to main)
- `IHostResource : ITestResource` — marker interface with `IHost Host { get; }`
- `HostResource` — factory-based (`Func<Task<IHost>>`), takes optional reset callback
- `HostResource<TProgram>` — uses `Host.CreateApplicationBuilder()`, TProgram is a lookup marker
- `HostResourceExtensions` — `GetHost(name?)` and `GetHostService<T>(name?)` on `IStepContext`
- C#'s `class` generic constraint accepts interface types, so `GetResource<IHostResource>()` works

### Sample Specs (PR #5 merged, PRs #6/#7 have conflicts — need reconciliation)
- 11 sample projects copied from `~/code/critterstacksamples` into `samples/`
- Feature files and fixture classes created for all 11 projects
- **Not yet wired up to compile/run** — the feature files and fixtures exist but the projects don't have Bobcat references, runner setup, or test project structure
- PR #7 attempted full wiring (101/101 passing) but was on a worktree branch that conflicts with main

## Architecture Decisions

### Extension Method Pattern (not base classes)
- Fixtures are loose — users compose their own, likely using multiple AlbaHosts
- Extension methods on `IStepContext` keep things flexible
- State tracked as fixture instance fields (new instance per scenario = automatic reset)

### IHostResource as the Integration Point
- Wolverine/Marten extensions don't own the host — they just find one via `IHostResource`
- Both `HostResource` and `AlbaResource` implement `IHostResource`
- Enables Wolverine extensions to work against an Alba-managed host seamlessly

### No Storyteller-style State Bag (Yet)
- Storyteller had `Context.State` — a per-spec dictionary keyed by type or type+name
- Bobcat currently relies on fixture instance fields for step-to-step state
- `AlbaResource.LastResult` tracks the most recent `IScenarioResult`
- Cross-fixture state bag is a future Bobcat core addition if needed after more real-world usage

### Planned Package Hierarchy
```
Bobcat (core)
├── IHostResource, HostResource, HostResource<T>
├── ITestResource, TestSuite, BobcatRunner
├── Fixture, Attributes, Source Generator
│
Bobcat.Alba
├── AlbaResource, AlbaResource<T> (implements IHostResource)
├── AlbaStepContextExtensions, AlbaAuthExtensions
│
Bobcat.Wolverine
├── WolverineStepContextExtensions (extension methods only, targets IHostResource)
│
Bobcat.Marten (TODO)
├── MartenStepContextExtensions
├── CleanAllMartenDataAsync, QueryByIdAsync, FetchStreamAsync, etc.
│
Bobcat.CritterStack (TODO — assumes Wolverine + Marten together)
├── Combined patterns: tracked sessions + event store assertions
├── Aggregate handler testing, projection wait helpers
```

## Sample Projects Status

| Project | Feature File | Fixture | Wired Up | Tests Pass |
|---------|-------------|---------|----------|------------|
| CqrsMinimalApi | ✅ | ✅ | ✅ | ✅ (5/5) |
| CleanArchitectureTodos | ✅ | ✅ | ❌ | ❌ |
| BankAccountES | ✅ | ✅ | ❌ | ❌ |
| EcommerceMicroservices | ✅ | ✅ | ❌ | ❌ |
| OutboxDemo | ✅ | ✅ | ❌ | ❌ |
| MoreSpeakers | ✅ | ✅ | ❌ | ❌ |
| BookingMonolith | ✅ | ✅ | ❌ | ❌ |
| EcommerceModularMonolith | ✅ | ✅ | ❌ | ❌ |
| MeetingGroupMonolith | ✅ | ✅ | ❌ | ❌ |
| PaymentsMonolith | ✅ | ✅ | ❌ | ❌ |
| ProjectManagement | ✅ | ✅ | ❌ | ❌ |

### Blockers for Running Sample Specs
1. **No Bobcat project references** — sample .csproj files don't reference Bobcat, Bobcat.Alba, or Bobcat.Generators
2. **No runner setup** — samples need a `Program.cs` (or separate test project) that calls `BobcatRunner.Run()` with `AlbaResource` registered on `TestSuite`
3. **Fixture API mismatch** — generated fixtures use extension methods (`PostJsonAsync`, `GetJsonAsync`) that need `IStepContext` passed in, but the source generator doesn't inject `IStepContext` as a parameter to step methods. Fixtures need to access context via `this.Context` property instead.
4. **PostgreSQL required** — all sample projects use Wolverine/Marten which need a running PostgreSQL instance. Docker-compose from critterstacksamples provides this on port 5432.
5. **`[Check]` attribute** requires a string argument (the step text), not bare like `[Fact]`

### What CqrsMinimalApi Wiring Revealed
- The Bobcat source generator produces a `{Feature}_Feature.g.cs` file
- Steps are matched by Cucumber Expression patterns in `[Given]`/`[When]`/`[Then]` attributes
- The generator passes parsed arguments (string, int, etc.) to fixture methods but does NOT pass `IStepContext`
- Fixtures must use `this.Context` (the `Fixture.Context` property, which is `IStepContext?`) to access resources
- `AlbaHost.For<Program>()` with Wolverine crashes with a native error if PostgreSQL isn't running — Wolverine's startup is not graceful without a database

## ConsolePreview Output (confirmed working)
The built-in demo runs 3 features (Calculator, Inventory, Invoicing) with 8 scenarios.
- 7/8 pass (1 deliberate failure to demo set verification error reporting)
- Set verification tables render beautifully with OK/FAIL/MISSING/EXTRA status columns
- Spectre.Console rendering with checkmarks, timing, and summary counts

## Key Files Reference

### Bobcat Core
- `src/Bobcat/Runtime/ITestResource.cs` — `Name`, `Start()`, `ResetBetweenScenarios()`, `DisposeAsync()`
- `src/Bobcat/Runtime/IHostResource.cs` — extends ITestResource with `IHost Host`
- `src/Bobcat/Runtime/HostResource.cs` — both generic and non-generic implementations
- `src/Bobcat/Runtime/TestSuite.cs` — resource registry and lifecycle
- `src/Bobcat/Engine/IStepContext.cs` — `GetResource<T>()`, `GetService<T>()`, `Log()`, `AttachDiagnostic()`
- `src/Bobcat/Engine/ExecutionContext.cs` — `SpecExecutionContext` implementation
- `src/Bobcat/Fixture.cs` — base class with `Context` property, `SetUp()`, `TearDown()`
- `src/Bobcat/Runtime/BobcatRunner.cs` — CLI entry point, `Run()` and `ScanForFeatures()`

### Bobcat.Alba
- `src/Bobcat.Alba/AlbaResource.cs` — both generic and non-generic
- `src/Bobcat.Alba/AlbaStepContextExtensions.cs` — HTTP helper extensions
- `src/Bobcat.Alba/AlbaAuthExtensions.cs` — auth helper extensions

### Bobcat.Wolverine
- `src/Bobcat.Wolverine/WolverineStepContextExtensions.cs` — tracked session extensions

### Alba API Notes
- `AlbaHost.For<TProgram>(params IAlbaExtension[])` — async factory
- `AlbaHost.For(WebApplicationBuilder, Action<WebApplication>, params IAlbaExtension[])` — builder+routes factory (good for test projects without a real Program)
- `IAlbaHost.Scenario(Action<Scenario>)` → `Task<IScenarioResult>`
- `IScenarioResult.ReadAsJsonAsync<T>()` — deserialize response
- `s.Get.Url(url)`, `s.Post.Json(body).ToUrl(url)`, `s.Put.Json(body).ToUrl(url)`, `s.Delete.Url(url)`
- `s.StatusCodeShouldBeOk()` (200), `s.StatusCodeShouldBe(int)`, `s.StatusCodeShouldBeSuccess()` (2xx)
- `s.WithClaim(type, value)` — singular, not `WithClaims`
- Alba 8.5.0, targets net8.0/net9.0/net10.0
- `BeforeEach`/`AfterEach` hooks are additive (appended to a list), not last-wins

### Wolverine Tracking API
- Namespace: `Wolverine.Tracking`
- `host.InvokeMessageAndWaitAsync(message)` → `Task<ITrackedSession>`
- `host.InvokeMessageAndWaitAsync<T>(message)` → `Task<(ITrackedSession, T)>`
- `host.SendMessageAndWaitAsync(message)` → `Task<ITrackedSession>`
- `host.ExecuteAndWaitAsync(Func<IMessageContext, Task>)` → `Task<ITrackedSession>`
- Session assertions: `session.Sent.SingleMessage<T>()`, `session.Executed.SingleHandler<T>()`
- In main `WolverineFx` package (no separate testing package)

## Next Steps (filed as GitHub issues)
1. Wire up sample specs to actually compile and run (need Bobcat refs, runner setup, PostgreSQL)
2. Build `Bobcat.Marten` package (MartenStepContextExtensions)
3. Build `Bobcat.CritterStack` package (combined Wolverine + Marten)
4. Revisit state management pattern after more real-world usage
5. Consider adding Storyteller-style `Context.State` bag to Bobcat core

---

# Update — April 29, 2026

## Status snapshot
- `main` HEAD: `3ac44fa` ("CqrsMinimalApi spec passes 5/5 — refactor to RESTful Guid contract (Path A)")
- Working tree clean, in sync with `origin/main` — no uncommitted work to flush.
- All in-flight Bobcat work has been parked and the remaining items are tracked as GitHub issues.

## What landed since the April 12 dump
- `7e6f4f1` Wire CqrsMinimalApi sample to the BobcatRunner end-to-end — first sample that
  actually compiles, runs against a live PostgreSQL, and exercises the Alba + Wolverine
  step-context extensions through the full BobcatRunner CLI path.
- `3ac44fa` CqrsMinimalApi spec passes 5/5 — refactor to RESTful Guid contract (Path A)
  was the sample-side change to expose stable Guid resource ids over a clean REST surface
  so the spec could assert against id-stable URIs instead of in-memory positional refs.

## PAL_SEHException root cause + fix (resolved)
A spurious native crash on Apple-silicon hosts was traced to two collisions when the
sample app and SpecsRunner shared a process:
1. **TFM mismatch** — sample built `net9.0` while Bobcat built `net10.0`. The runtime
   loaded the wrong AOT-compiled native bits and aborted in PAL.
2. **Top-level-statements `Program` collision** — both projects emitted a synthesized
   `Program` class at the root namespace.
**Fix:** bumped the sample TFM to match Bobcat's, and converted SpecsRunner to an explicit
`Main` method in a named class so there's no synthesized `Program` to collide.

This is no longer an active blocker. If a similar PAL crash resurfaces, those two angles
are the right first checks.

## Postgres-on-arm64 note (cross-cut)
The CritterWatch docker-compose was switched from `postgis/postgis:17-3.4` (amd64-only
manifest) to `pgvector/pgvector:pg17` (multi-arch). Bobcat samples that run against the
shared local Postgres benefit from the same change — exit-code-126 on the postgres
container was masking what looked like Bobcat-specific failures.

## Open issues parked for the next session
- **#8** Wire up remaining 10 sample projects to BobcatRunner (template established by
  CqrsMinimalApi). The CqrsMinimalApi case study under `samples/CqrsMinimalApi/` is the
  reference pattern: project file references for Bobcat / Bobcat.Alba / Bobcat.Generators,
  a SpecsRunner with `BobcatRunner.Run()`, and an `AlbaResource<Program>` registration on
  the `TestSuite`.
- **#9** Build `Bobcat.Marten` — `MartenStepContextExtensions` analogous to the
  Wolverine extensions: `CleanAllMartenDataAsync`, `QueryByIdAsync`, `FetchStreamAsync`,
  etc., resolving `IDocumentStore` via the registered `IHostResource`.
- **#10** Build `Bobcat.CritterStack` — assumes Wolverine + Marten together. Combined
  helpers for tracked sessions + event-store assertions, aggregate-handler testing,
  projection wait helpers.
- **#11** Improve diagnostics for sample-wiring footguns + document the playbook so the
  next person wiring a sample doesn't have to rediscover the five blockers from the
  Sample Projects Status table above.

## Resuming in a new chat window
Bring this file plus the open issue numbers (#8 / #9 / #10 / #11). The bobcat repo
itself is at a clean stopping point, so resuming is "pick an issue, read the relevant
section here for context, go." No mid-flight branches to clean up.
