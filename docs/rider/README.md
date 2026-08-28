# Rider: the `reqnroll/Reqnroll.Rider` patch (issue #109)

`0001-bobcat-attributes.patch` is a `git format-patch` of one commit against
`reqnroll/Reqnroll.Rider` `main` (`747939a`, "Use Java 25 for Rider 2026.2 builds", fetched
2026-08-21). It is the "option A" change from `docs/editor-integration.md`: teach the Reqnroll
Rider plugin to discover Bobcat step definitions, so completion, go-to-definition, find-usages and
undefined-step highlighting work for `.feature` files in a project that references Bobcat and not
Reqnroll.

**SUBMITTED 2026-08-28 (Jeremy's call, made in session): [reqnroll/Reqnroll.Rider#92](https://github.com/reqnroll/Reqnroll.Rider/pull/92)**,
from `jeremydmiller:bobcat-attributes` (fork created the same day). Before submitting, the 2026.2
build gap was closed on this machine: `JetBrains.Rider.SDK` 2026.2.0 restores straight from
nuget.org, and the `./gradlew :prepare` guard in `src/dotnet/Directory.Build.props` only checks
that `build/DotNetSdkPath.Generated.props` exists — hand-writing it with
`<DotNetSdkPath>/Applications/Rider.app/Contents/lib/ReSharperHost</DotNetSdkPath>` let
`dotnet build` compile the full plugin **and** the test project with **0 errors** against the real
2026.2 SDK, no Gradle and no Java. The net472 test host still crashes outside a Windows/Rider test
environment (upstream CI's job), and no `:runIde` sandbox session was run. The commands below are
kept for re-submission or the fork path.

## What it changes

Five files, +186/−9, in `src/dotnet/`:

| File | Change |
|---|---|
| `ReSharperPlugin.ReqnrollRiderPlugin/Helpers/ReqnrollAttributeHelper.cs` | `Bobcat.GivenAttribute` / `WhenAttribute` / `ThenAttribute` added to the three CLR-name arrays, `Bobcat.CheckAttribute` added to Then; `CheckAttributeShortName = "Check"` accepted as Then on the short-name (source) path; new `IsStepAttribute(fullName)`, `IsStepAttributeShortName(shortName)`, `CanContainBobcatSteps(IMetadataAssembly)`; `GetAttributeStepKind` returns `null` for Bobcat attributes so the "method name does not match pattern" inspection (Reqnroll's `GivenXxx` convention, a HINT on every method) stays off Bobcat fixtures. `GetAttributeClrName` still answers `Reqnroll.*`, so the create-step quick fix generates what it always did. |
| `…/Caching/StepsDefinitions/AssemblyStepDefinitions/AssemblyStepDefinitionCache.cs` | A type with no `[Binding]` is admitted when a public method carries a step attribute — but only when the assembly *is* or *references* `Bobcat`, so no other referenced assembly pays for a per-method scan. The class entry records whether `[Binding]` was present instead of hard-coding `true`. |
| `…/Caching/StepsDefinitions/ReqnrollStepsDefinitionsCache.cs` | A class with no `[Binding]` and not `partial` is admitted when a method attribute with one argument has a step short name (`Given`/`When`/`Then`/`StepDefinition`/`Check`). Syntactic, like the method walk it feeds — attribute types cannot be resolved while this cache is being built. `VersionInt` 15 → 16 so a persisted cache is rebuilt under the new rule. |
| `ReSharperPlugin.ReqnrollRiderPlugin.Tests/Helpers/ReqnrollAttributeHelperTests.cs` | New: 39 NUnit cases over the helper — Bobcat names map to kinds, `Check` is a Then and nothing else, Reqnroll/SpecFlow names and the generated names are unchanged, the naming inspection skips Bobcat. |
| `CHANGELOG.md` | `## Unreleased` entry. |

Reqnroll and SpecFlow behaviour is unchanged — same names, same `[Binding]` gate, same generated
attributes. Two deliberate widenings to disclose in the PR: a class that declares steps without
`[Binding]` is now indexed (the plugin already admitted any `partial` class on those terms), and a
`[Check("...")]` attribute on a method is read as a Then by short name in source, exactly as
`[Given]`/`[When]`/`[Then]` already are.

Known gaps after this patch, by design: `[TableGrammar]` (class-level expression — Rider reads
method attributes only), and the create-missing-step quick fix still generates `Reqnroll.*`
attributes and only offers `[Binding]` classes as targets. Both are follow-ons.

## How it was verified — and how it was not

- **Helper compiled and its 39 tests run green** against the metadata-reader DLLs of the locally
  installed Rider 2025.1 (`/Applications/Rider.app/Contents/lib/ReSharperHost/`), in a scratch
  net10.0 NUnit project that links the patched `ReqnrollAttributeHelper.cs`, `GherkinStepKind.cs`
  and the new test file. That exercises the real `ClrTypeName`, `IClrTypeName`,
  `IMetadataAssembly.ReferencedAssembliesNames` and `AssemblyNameInfo.Name`.
- **Both patched caches type-check** against the same Rider's PSI and metadata DLLs (stubbing only
  the two injected plugin services, `IReqnrollStepInfoFactory` and
  `IUnderscoresMethodNameStepDefinitionUtil`). The only two errors are the expected ones: 2026.2
  changed `IAssemblyCache`'s shape (`IsApplicable` / `GetBuildParameters` / four-argument `Build`),
  which 2025.1 does not have. Every line the patch touches — `IMetadataTypeInfo.GetMethods()`,
  `IMetadataEntity.CustomAttributesTypeNames[i].FullName.GetText()`, `IMetadataMethod.IsPublic`,
  `IClassDeclaration.MethodDeclarations`, `IAttribute.Arguments.Count`, `IAttribute.Name.ShortName`
  — resolved.
- `git apply --check` of the patch against `origin/main` is clean.
- **Not done:** the plugin was **not built with the 2026.2 Rider SDK** and **not run in a Rider
  sandbox**. The real build is `./gradlew :prepare` (downloads the Rider SDK and the IDE
  distribution, several GB) then `dotnet build` of `src/dotnet/`, and needs Java 25 per the
  repository's latest commit; this machine has neither Java nor the SDK, so it was skipped as
  outside the ~10-minute budget. Jeremy should build and open a Bobcat solution in the sandbox
  (`./gradlew :runIde`) before submitting. The `testProjects/` folder has the plugin's own sample
  solutions; a copy of `samples/` from this repo is the Bobcat check.

## To submit (Jeremy)

The reference clone at `~/code/Reqnroll.Rider` already has the commit on branch
`bobcat-attributes`, authored as Jeremy. Either push that branch, or re-apply the patch anywhere:

```bash
# 1. fork (once) and add it as a remote
gh repo fork reqnroll/Reqnroll.Rider --clone=false
cd ~/code/Reqnroll.Rider
git remote add fork git@github.com:jeremydmiller/Reqnroll.Rider.git

# 2. the branch exists already; to rebuild it from the patch instead:
#    git checkout -b bobcat-attributes origin/main && git am ~/code/bobcat/docs/rider/0001-bobcat-attributes.patch

# 3. build and try it (Java 25 + Gradle wrapper; first run downloads the SDK)
./gradlew :prepare && dotnet build src/dotnet/ReSharperPlugin.ReqnrollRiderPlugin.Tests/
./gradlew :runIde     # open a Bobcat solution, type a step in a .feature, Ctrl-B on it

# 4. push and open the PR
git push -u fork bobcat-attributes
gh pr create --repo reqnroll/Reqnroll.Rider --head jeremydmiller:bobcat-attributes \
  --title "Discover Bobcat step definitions alongside Reqnroll and SpecFlow" \
  --body-file ~/code/bobcat/docs/rider/pr-body.md
```

`pr-body.md` is a draft of the PR description, next to the patch. If the maintainers would rather
not carry a third framework's names, option B in `docs/editor-integration.md` (a settings-backed
name list) is the fallback offer; if the PR stalls, the issue's plan is to fork as "Bobcat for
Rider".
