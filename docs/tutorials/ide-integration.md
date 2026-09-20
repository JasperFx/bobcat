# Integrating Bobcat with Your IDE

**What you will get:** scenarios as individual entries in your IDE's test explorer, runnable and
debuggable one at a time — plus step completion and go-to-definition in `.feature` files where the
editor supports it.

These are two separate mechanisms with different answers per editor, so they are worth taking in
turn.

## Part 1 — scenarios in the test explorer

This works everywhere, in any IDE that can run .NET tests, and it costs one package.

`Bobcat.Mtp` exposes the spec project as a Microsoft.Testing.Platform test host, and **one scenario
is one test node**. `dotnet test`, Visual Studio, Rider and VS Code's C# Dev Kit all see scenarios
as ordinary tests — so you get the green arrow in the gutter, run-one-test, and the debugger.

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

No hand-written `Main` is needed — the generator detects `Bobcat.Mtp` and emits the entry point.

**One cost to know about:** resources start per run. An IDE that runs one test at a time pays host
and database startup each time, which is fine for a single scenario and painful for a loop. When
you are iterating, the `interactive` command keeps resources warm between runs instead — see
[The Command Line](../command-line.md).

Details, including configuring the suite with `[BobcatConfiguration]` and the case where the entry
point is *not* generated: [Running Specs with `dotnet test`](../dotnet-test.md).

## Part 2 — editing `.feature` files

Here the editors diverge sharply.

| Editor | Step completion & go-to-definition | Cost |
|---|---|---|
| **VS Code** | Works today | Install the Cucumber extension, commit three settings |
| **Rider** | Not yet — a Bobcat plugin of our own, unscheduled | Nothing available today |

### VS Code

1. Install **Cucumber** by CucumberOpen (`CucumberOpen.cucumber-official`).
2. Point it at your features and your step definitions. The extension's defaults look for
   `*specs*/**/*.cs`, which almost certainly matches nothing in your layout:

```jsonc
{
  "cucumber.features": ["**/*.feature"],
  "cucumber.glue": ["**/*Fixture.cs", "**/*Grammars.cs"]
}
```

The setting is `cucumber.glue`, not `glob`. It must cover **every** file that declares steps —
your fixtures, and any grammar module you compose in with `[IncludeGrammars]`.

You then get completion from every visible `[Given]`/`[When]`/`[Then]`, go-to-definition from a
step to its method, undefined-step underlines, and a generate-step-definition quick fix whose
emitted C# is valid Bobcat as written.

**The extension parses source, not assemblies.** A step defined in a referenced package is
undefined as far as the editor is concerned, which is why Bobcat ships its grammars as source
inside the packages — add those paths to `cucumber.glue` to pick them up.

Two kinds of step are invisible to it, because the extension's attribute list is a constant in the
library and cannot be configured:

- **`[Check]` steps.** The fix is to stack a `[Then]` with the same expression beside the
  `[Check]` — a supported idiom, pinned by tests in both orders:

  ```csharp
  [Then("the result is not negative")]     // what the editor sees
  [Check("the result is not negative")]    // what runs
  public bool TheResultIsNotNegative() => Result >= 0;
  ```

  Do **not** "fix" it by writing the check as a plain `[Then]` returning `bool` — a sentence step
  discards the bool, turning a failing check into a silent pass.

- **`[TableGrammar]` steps.** No zero-cost fix exists; the editor is simply blind to them, while
  the build is not.

### Rider

Rider needs a Bobcat plugin of our own, and it is not scheduled. There is an open upstream change
against Reqnroll's Rider plugin that would make Rider work for Bobcat users sooner if it lands.

In the meantime, Part 1 still applies in full: scenarios show up in Rider's test explorer through
`Bobcat.Mtp` and run and debug normally. It is only `.feature` *editing* support that is missing.

The complete investigation — what the VS Code extension matches, verified rather than assumed, and
exactly what blocks Rider — is [Editor Integration](../editor-integration.md).

## Where to go next

- Running the same suite in a pipeline — [Integrating Bobcat with CI](continuous-integration.md)
