# Running Specs with `dotnet test`

`Bobcat.Mtp` exposes a spec project as a Microsoft.Testing.Platform test host: one scenario is
one test node, so `dotnet test`, IDE Test Explorers, CI, and the Bobcat supervisor all see
scenarios as ordinary tests.

## Zero-ceremony setup (the generated entry point)

Since issue #207, a spec project needs no hand-written `Main`. Reference the packages, add the
`.feature` files, make the project executable — done:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Bobcat" />
  <PackageReference Include="Bobcat.Mtp" />
  <PackageReference Include="Bobcat.Generators" />
  <AdditionalFiles Include="Features/**/*.feature" />
</ItemGroup>
```

`Bobcat.Generators` detects that the compilation references `Bobcat.Mtp` and declares no entry
point of its own, and emits one (`BobcatEntryPoint.g.cs`): a `Main` that goes through
`BobcatTestApplication.Run`, scans the assembly for generated features and code-first
specifications, and calls every `[BobcatConfiguration]` method.

### Configuring the suite

Most real suites register resources. Mark any static method with `[BobcatConfiguration]` and the
generated `Main` calls it — this is where a hand-written `Main`'s configure lambda would have
gone:

```csharp
public static class SuiteConfiguration
{
    [BobcatConfiguration]
    public static void Configure(BobcatRunner runner)
    {
        runner.Suite.AddResource(new AlbaResource<Program>());
        runner.RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 };
    }
}
```

The method must be `static void` with exactly one `BobcatRunner` parameter, reachable from
generated code (a wrong shape is a compile error, BOBCAT016). Several such methods are called in
a deterministic order — sorted by declaring type, then method name.

### When the entry point is NOT generated

The generator abstains, in order, when:

1. the compilation does not reference `Bobcat.Mtp`;
2. the MSBuild property `BobcatGenerateEntryPoint` is `false` (the opt-out);
3. the project is not an executable (`OutputType` must be `Exe`);
4. **the assembly declares its own entry point.** A hand-written `Main` always wins — every
   pre-#207 consumer keeps compiling unchanged, and CS0017 is impossible. A
   `[BobcatConfiguration]` method left behind in that situation is reported (warning BOBCAT017)
   rather than silently ignored, because the hand-written `Main` will not call it.

Hand-writing `Main` remains fully supported and is the escape hatch for anything the seam does
not cover:

```csharp
public static class SpecsRunner
{
    public static Task<int> Main(string[] args)
        => BobcatTestApplication.Run(args, runner =>
        {
            runner.ScanForFeatures(typeof(SpecsRunner).Assembly);
            runner.Suite.AddResource(new AlbaResource<Program>());
        });
}
```

In-repo `ProjectReference` consumers do not inherit `Bobcat.Mtp`'s `buildTransitive` props, so
they must set `<GenerateTestingPlatformEntryPoint>false</GenerateTestingPlatformEntryPoint>`
themselves (that is the platform's synthesized entry point, a different thing from Bobcat's).

## Filtering

The host is runnable directly (`./MySpecs`) and through `dotnet test`; arguments after `--` go
to the test host:

```bash
# The platform's own uid filter — one scenario, exactly
dotnet test -- --filter-uid "Ordering/An order is accepted"

# Friendly filters (issue #207): by feature title (case-insensitive substring) …
dotnet test -- --filter-feature "Ordering"

# … and by Gherkin tag (case-insensitive, exact; write the tag without the @ —
# the platform consumes @-prefixed arguments as response files)
dotnet test -- --filter-tag regression

# They intersect, and both narrow --list-tests too
./MySpecs --list-tests --filter-feature "Shipping"
```

These are the same levers `ConsolePreview run --feature/--tag` offers in-process, with the same
semantics, and they work against hand-written-`Main` hosts too — the options register inside
`BobcatTestApplication.Run`.

## Resources under IDE runs — a cost to know about

Discovery never starts resources (IDEs discover on every build). **Execution always pays the
suite's full resource start-up, however few scenarios were selected**: running one scenario from
Test Explorer still runs `StartAll` for every registered resource — the database, the Docker
containers, the application host. That is inherent to the model — a scenario's meaning includes
the resources it runs against, and Bobcat will not guess which ones a subset needs — so budget a
single-scenario IDE run at roughly resource start-up plus the scenario, not the scenario alone.
Keeping `Start()` fast (reuse running containers, `docker compose up -d` out of band) is the
lever that matters.
