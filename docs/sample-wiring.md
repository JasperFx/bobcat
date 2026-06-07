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
3. Make the fixture extend `Bobcat.Fixture` and use `Context!` (not a stored field).
4. **Bring the host API and the fixture into agreement.** This is where the work usually goes —
   fixtures often describe a clean RESTful contract while host endpoints are RPC-style. Refactor
   the host (Path A) rather than weakening the spec.
5. Drop and recreate the host's Marten schema before the first run (old shape may conflict).

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

### 4. Alba's default 200 assertion
Alba's default `Scenario(...)` asserts a 200 status. The `Bobcat.Alba` helpers call
`s.IgnoreStatusCode()` for you and surface the real status on `HttpResult`, but a sample that
reaches into Alba directly will trip on non-200 paths (201/204/404).
