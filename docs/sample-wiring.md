# Sample Wiring Playbook

How to wire a sample host to `BobcatRunner` so its `.feature` specs run end-to-end through
Alba. This is the canonical reference for issue #8; the reference implementation is
`samples/CqrsMinimalApi/Tests/`.

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
