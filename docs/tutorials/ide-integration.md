# Integrating Bobcat with Your IDE

**What you will get:** your specifications as individual entries in your IDE's test explorer,
runnable and debuggable one at a time -- because they are ordinary tests.

## Specifications in the test explorer

A Bobcat specification is a test in your own runner, so Visual Studio, Rider and VS Code's C# Dev Kit
see it the way they see any other test: the green arrow in the gutter, run-one-test, and the debugger.
There is no Bobcat-specific explorer integration to install.

What *is* needed is what any xUnit v3 project needs for an IDE to find its tests:

```xml
<ItemGroup>
  <PackageReference Include="xunit.v3" />
  <PackageReference Include="Microsoft.NET.Test.Sdk" />
  <PackageReference Include="xunit.runner.visualstudio" />
</ItemGroup>
```

Leave out `Microsoft.NET.Test.Sdk` and the project builds, runs and passes from the command line and
shows **no tests** in Rider or Visual Studio, which discover tests through the VSTest bridge those two
packages provide. Nothing about the build says so on its own -- which is how a real spec project
shipped without it -- so Bobcat warns with **BOBCAT033**. Set
`<BobcatSuppressTestSdkWarning>true</BobcatSuppressTestSdkWarning>` if the suite only ever runs from
the command line.

**Your fixtures start per run.** An IDE that runs one test at a time pays for whatever your assembly
and collection fixtures stand up -- the host, the database -- each time. That's the same cost any
integration test pays, and keeping fixture setup fast (reusing running containers, `docker compose up
-d` out of band) is the lever that matters.

## Reading the specification while you work

Run a single specification from the IDE with a terminal attached and nothing listening on the run
wire, and the run prints the specification it produced -- see
[Reading the specification the run produced](../marker-steps.md#reading-the-specification-the-run-produced).
An IDE usually captures the output stream, so set `BOBCAT_SPEC_CONSOLE=1` in your run configuration to
print it anyway.

A failing Acceptance spec (`Lifecycle = Lifecycle.Acceptance`) shows in the test explorer as
**skipped**, like any other skip; the reason text -- `acceptance spec, not yet passing: …` -- is what
tells it apart. A `Pending = true` stub is skipped too, before it runs.

## Navigation

Step helpers, marker comments and the scenario vocabulary are all plain C#, so go-to-definition, find
usages and rename work on them with no plugin. A step's text lives in the attribute on the method it
describes, one keystroke away from every call.

## Where to go next

- Running the same suite in a pipeline -- [Integrating Bobcat with CI](continuous-integration.md)
