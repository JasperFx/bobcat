# Editor integration — step completion and go-to-definition (issue #109)

Status, 2026-08-21:

| Editor | Status | What it costs |
|---|---|---|
| **VS Code** | Works today, zero Bobcat code | Install the official Cucumber extension, commit three settings (`.vscode/settings.json` in this repo is the sample) |
| **Rider** | Patch ready, not submitted | `docs/rider/0001-bobcat-attributes.patch` against `reqnroll/Reqnroll.Rider` (+186/−9, 5 files); helper compiled and tested, caches type-checked, **not built with the 2026.2 SDK or run in Rider**; submitting is Jeremy's call — `docs/rider/README.md` has the commands. Meanwhile, a `partial` fixture in the same solution is very likely already visible to the shipped plugin (source reading, below) |

Decision of record from the issue still stands: no Reqnroll package dependency (there is no
attributes-only package) and no namespace-squatting of `Reqnroll.GivenAttribute`. Everything
below works with Bobcat's own attributes as they are.

## What the VS Code extension actually matches — verified, not assumed

The official extension (`CucumberOpen.cucumber-official`, 1.11.0) delegates to
`@cucumber/language-service` (1.7.0), which finds C# step definitions with two tree-sitter
queries in `src/language/csharpLanguage.ts`:

```scheme
(method_declaration
  (attribute_list
    (attribute
      name: (identifier) @annotation-name
      (attribute_argument_list
        (attribute_argument
          (verbatim_string_literal) @expression))))     ; second query: (string_literal)
  (#match? @annotation-name "Given|When|Then|And|But|StepDefinition")
) @root
```

Three things follow from the query text, and all three were confirmed by running the real
library over this repo's fixtures (the harness is described at the end):

1. **It keys on the attribute's short name, syntactically.** `[Given("...")]` is an `identifier`
   named `Given`; it matches. It never resolves the type, never needs a `[Binding]` on the class,
   and never looks at the project file, so a class with no Reqnroll reference anywhere is
   indistinguishable from a Reqnroll binding class. That is the whole reason this is zero-code.
2. **`#match?` is an unanchored regex**, so `[GivenAttribute("...")]` matches too — and so would
   any attribute whose name merely *contains* `Then`. `Check` and `TableGrammar` contain none of
   the six words.
3. **The expression must be a plain or verbatim string literal.** A plain `"..."` is parsed as a
   **Cucumber Expression**; a verbatim `@"..."` is parsed as a **regular expression** (after
   un-doubling `""`). That is the only place the literal's shape matters.

Every attribute shape, against the real query:

| Shape | Seen? | Parsed as | Why |
|---|---|---|---|
| `[Given("the left operand is {int}")]` | yes | Cucumber Expression | the normal case |
| `[Given(@"^a regex (\d+)$")]` | yes | regular expression | `verbatim_string_literal` branch |
| `[GivenAttribute("...")]` | yes | Cucumber Expression | unanchored `#match?` |
| `[Given("...", Order = 1)]` | yes | Cucumber Expression | any `attribute_argument` that is a literal |
| `[Given("x")]` + `[When("x")]` on one method | yes, twice | — | one link per attribute |
| `[Then("x")]` + `[Check("x")]` on one method | yes, once | via the `[Then]` | see the `[Check]` section |
| `[Bobcat.Given("...")]` | **no** | — | `qualified_name`, not `identifier` |
| `[Given("""raw string""")]` | **no** | — | `raw_string_literal` is not in the query |
| `[Given(SomeConst)]`, `[Given($"...")]` | **no** | — | not a literal |
| `[Check("...")]` | **no** | — | name does not match |
| `[TableGrammar("...")]` on a class | **no** | — | `class_declaration`, not `method_declaration`; name does not match either |

**Measured on this repo (28 fixture/grammar files):** 186 of 186 `[Given]`/`[When]`/`[Then]`
expressions were found once the `{decimal}` parameter type was registered (181 without it — see
settings); **all 35 `[Check]`** and **all 8 `[TableGrammar]`** were invisible. Every expression
in this repo is a plain literal; no raw strings, constants, or verbatim regexes are in use.

Two smaller findings from the same run:

- **`{decimal}` is not a built-in parameter type.** Cucumber Expressions ship `int`, `float`,
  `double`, `long`, `word`, `string`, and a few more — not `decimal`, which Bobcat's
  `CucumberExpressionParser` supports. Without a `cucumber.parameterTypes` entry, every step
  using it is reported as an invalid expression ("Undefined parameter type 'decimal'") and is
  not offered for completion. The setting below fixes it.
- **An escaped quote (`\"`) inside an expression is mis-parsed** — `unescapeString` upstream only
  handles `\\`, and a bare `\` is an escape character to Cucumber Expressions. No Bobcat fixture
  does this today; use `{string}` instead of embedding quotes.

## VS Code setup

1. Install **Cucumber** by CucumberOpen (`CucumberOpen.cucumber-official`). This repo's
   `.vscode/extensions.json` recommends it, so a fresh clone is prompted.
2. Point it at the features and the fixtures. The extension's defaults look for
   `*specs*/**/*.cs` and `*specs*/**/*.feature`, which matches nothing here. The committed
   `.vscode/settings.json`:

```jsonc
{
  "cucumber.features": [
    "src/*/Features/**/*.feature",
    "samples/*/Features/**/*.feature"
  ],
  "cucumber.glue": [
    "src/*/*.cs",
    "samples/*/*.cs"
  ],
  "cucumber.parameterTypes": [
    { "name": "decimal", "regexp": "-?[\\d.]+" }
  ]
}
```

- The setting is **`cucumber.glue`**, not `cucumber.glob` as the issue text has it.
- `cucumber.glue` must cover every file that declares steps — fixtures, grammar modules
  composed through `[IncludeGrammars]`, and `src/Bobcat/ClockGrammars.cs`, the clock grammar
  that ships inside the core assembly. The extension parses **source only**: a step defined in a
  referenced assembly whose source is not in the workspace is undefined as far as the editor is
  concerned. Inside this repo that is not a problem because the source is here. A consumer
  taking Bobcat from NuGet will see `Given the date is "..."` underlined as undefined unless the
  grammar source is in their workspace — shipping the grammars as source is the fix, and is
  why the issue notes "the shipped grammars ship as source". **Done for `Bobcat.CritterStack`**
  (issue #104): its grammar `.cs` travels in the package under `contentFiles/cs/` (buildAction
  `None`, so it is never double-compiled against the assembly) and `content/grammars/`, so a
  consumer can point `cucumber.glue` at it. The Bobcat **generator** still needs no source — it
  reads a base fixture's steps from assembly metadata — this is purely for the editors. The core
  `ClockGrammars` is not yet source-shipped the same way.
- Globs are expanded one at a time with `fast-glob`, so a `!**/obj/**` entry excludes nothing.
  The sample stays one directory level deep instead, which is where every fixture in this repo
  lives and keeps `bin/` and `obj/` out.
- For your own project, the minimal equivalent is usually
  `"cucumber.features": ["**/*.feature"]` and `"cucumber.glue": ["**/*Fixture.cs", "**/*Grammars.cs"]`
  (plus whatever you name grammar modules), and the `decimal` parameter type if you use it.

### What works

- **Completion** of step text in `.feature` files, from every visible `[Given]`/`[When]`/`[Then]`.
- **Go to definition** from a step to the attributed method.
- **Undefined-step** diagnostics (underline) for steps no expression matches.
- **Generate step definition** quick fix. Its C# template is SpecFlow-flavoured (the comment links
  to `CucumberExpressions.SpecFlow`), but the emitted `[Given("...")] public void ...(...)` is a
  valid Bobcat step as written.
- Gherkin syntax highlighting, formatting, outline.

### What does not

- **`[Check]` steps are invisible.** The editor underlines a `Then` that a check satisfies as
  undefined, and go-to-definition has nowhere to go. Workaround below.
- **`[TableGrammar]` steps are invisible.** Class-level attribute, unknown name — nothing the
  current query can see.
- **Steps defined only in a referenced assembly** (see above).
- **Raw string literals, constants, interpolated strings** as the expression.
- Test running and debugging — the extension offers neither for C#; use the MTP host
  (`Bobcat.Mtp`) and the IDE's Test Explorer for that.

### `[Check]` — the workaround, and why the generator now guarantees it

The query cannot be configured: the extension's settings are `features`, `glue` and
`parameterTypes`, and the attribute-name list is a string constant in the library. So the
cheapest fix is on Bobcat's side: **stack a `[Then]` with the same expression next to the
`[Check]`.**

```csharp
[Then("the result is not negative")]     // what the editor sees
[Check("the result is not negative")]    // what runs
public bool TheResultIsNotNegative() => Result >= 0;
```

Before this change the generator's attribute loop let the *last* attribute win, so that stack
was a check only if `[Check]` came second — and a `[Then]` returning `bool` with no expected
capture is a plain sentence step that **discards the bool**, which would have turned a failing
check into a silent pass. `BobcatGenerator.extractStepMethod` now lets `[Check]` win in either
order; `Bobcat.Acceptance.Tests/EditorVisibleCheckTests` pins it from both directions. The
stack is therefore a supported idiom, not an accident of ordering.

The alternative the issue floated — writing checks as `[Then]` directly — is exactly the
bool-discarding footgun above, so do not.

### `[TableGrammar]` — no zero-cost fix

Nothing on the editor side helps: the query is method-only and name-gated, and neither is
configurable. Options, cheapest first:

1. **Accept it.** A table grammar's step text is a sentence the author wrote on the class; the
   feature file still compiles or fails at build time exactly as before. Only the editor is blind.
2. **Bobcat-side (not built):** let the grammar's expression be declared with a
   `[Given]`/`[When]`/`[Then]` on the `Row` method *instead of* `[TableGrammar]` on the class,
   and have the generator treat a class whose `Row` carries a step attribute as a table grammar.
   That keeps one source of truth and makes it visible through the existing query. Moderate
   generator change (`TableGrammarInfo` discovery + `StepMatcher.MatchTableGrammar`); not
   attempted here because it changes the authoring surface and deserves its own decision.
3. **Upstream (unlikely to land):** a `cucumber.stepAttributeNames` setting would help every
   wrapper library, but `cucumber/language-service` has kept the list a constant across ten
   languages and a PR to make one language's list configurable would be out of character.

## Rider — what blocks it, and the proposed upstream change

Read from `reqnroll/Reqnroll.Rider` `main` (MIT; last push 2026-07-27; Kotlin front-end +
ReSharper back-end on `net472`; references `Cucumber.CucumberExpressions` 17.1.0, so **Cucumber
Expressions are supported** — `ReqnrollStepInfoFactory.Create` tries `CucumberExpression` first
and falls back to regex; the README's "regex only" limitation is stale).

Step definitions are discovered by two caches, and they gate differently:

| Path | File | Class gate | Method gate |
|---|---|---|---|
| Source in the solution | `Caching/StepsDefinitions/ReqnrollStepsDefinitionsCache.cs` | class has `[Binding]` resolving to `Reqnroll.BindingAttribute` / `TechTalk.SpecFlow.BindingAttribute`, **or** an *unresolved* `[Binding]`, **or** the class is `partial` | attribute **short name** `Given` / `When` / `Then` / `StepDefinition` with exactly one constant-string argument (`ReqnrollAttributeHelper.IsAttributeForKindUsingShortName`) |
| Referenced assemblies | `Caching/StepsDefinitions/AssemblyStepDefinitions/AssemblyStepDefinitionCache.cs` | type carries `[Binding]` by **full CLR name** (`ReqnrollAttributeHelper.IsBindingAttribute`) | attribute **full CLR name** in `ReqnrollAttributeHelper.GivenAttribute` / `WhenAttribute` / `ThenAttribute` / `StepDefinitionAttribute` |

So Bobcat's *method* attributes already pass the source path; the *class* gate is what blocks a
fixture, and the assembly path blocks everything — including the shipped `ClockGrammars`, which
is the case the issue's acceptance criterion names. The names all live in one place:

```csharp
// src/dotnet/ReSharperPlugin.ReqnrollRiderPlugin/Helpers/ReqnrollAttributeHelper.cs
public static readonly ClrTypeName[] BindingAttribute        = [new("Reqnroll.BindingAttribute"),        new("TechTalk.SpecFlow.BindingAttribute")];
public static readonly ClrTypeName[] StepDefinitionAttribute = [new("Reqnroll.StepDefinitionAttribute"), new("TechTalk.SpecFlow.StepDefinitionAttribute")];
public static readonly ClrTypeName[] GivenAttribute          = [new("Reqnroll.GivenAttribute"),          new("TechTalk.SpecFlow.GivenAttribute")];
public static readonly ClrTypeName[] WhenAttribute           = [new("Reqnroll.WhenAttribute"),           new("TechTalk.SpecFlow.WhenAttribute")];
public static readonly ClrTypeName[] ThenAttribute           = [new("Reqnroll.ThenAttribute"),           new("TechTalk.SpecFlow.ThenAttribute")];
public const string StepDefinitionAttributeShortName = "StepDefinition";
public const string GivenAttributeShortName = "Given";   // + When, Then
```

Every consumer of those names (from a code search of the repo):

- `AssemblyStepDefinitionCache.Build` — class gate + method gate, full names.
- `ReqnrollStepsDefinitionsCache.HasBindingAttribute` / `AddToCacheEntryBasedOnAttributeRegex` —
  class gate by full name or unresolved short name; method gate by short name.
- `Daemon/MethodNameMismatchPattern/...RecursiveElementProcessor.cs` — `GetAttributeStepKind` by
  full CLR name (the "rename method to match pattern" inspection).
- `QuickFixes/CreateMissingStep/CreateReqnrollStepUtil.cs` and
  `CreateReqnrollStepFromUsageAction.cs` — generate `Reqnroll.*` attributes for a new step, and
  choose target classes via `GetBindingTypes`, which requires a `[Binding]`.
- `Caching/StepsDefinitions/ScopeAttributeReader.cs` — `[Scope]` only; irrelevant to Bobcat.

Package gating, for completeness: `Extensions/ProjectExtensions.IsReqnrollProject` (assembly or
package named `Reqnroll`) is used only by `ProjectRefresher` (reload after build) and analytics,
and `UnitTestExplorers/ReqnrollUnitTestProvider.IsSupported` gates *test running* on a
`Reqnroll`/`TechTalk.SpecFlow` assembly reference. Navigation and completion are not gated on the
package as far as the source shows. **None of this was verified in a running Rider** — it is
read from the code (clone at `~/code/Reqnroll.Rider`, `main` = `747939a`, 2026-08-21).

### The cheap experiment: does `partial` bypass the source gate? Yes — by source reading

The question was whether declaring a fixture `public partial class CalculatorFixture : Fixture`
already makes its steps visible to the shipped plugin, without any upstream change. Traced through
the source, with the plugin as it is on `main`:

1. **Admission.** `Caching/StepsDefinitions/ReqnrollStepsDefinitionsCache.cs:98-100` —
   `var hasBindingAttribute = HasBindingAttribute(classDeclaration); if (!hasBindingAttribute && !classDeclaration.IsPartial) continue;`
   A `partial` class is admitted with `hasBindingAttribute == false`; the only other exclusion
   (`IsReqnrollFeatureFile`, line 101) is for `[GeneratedCode("Reqnroll", …)]` code-behind.
2. **Method gate.** `ReadStepsFromMethodsOfClass` (lines 246-277) walks every method; an attribute
   with **exactly one argument** goes to `AddToCacheEntryBasedOnAttributeRegex` (lines 279-298),
   which requires the argument to be a **constant string** and matches
   `attribute.Name.ShortName` against `Given` / `When` / `Then` / `StepDefinition` via
   `Helpers/ReqnrollAttributeHelper.cs:49-60` (`IsAttributeForKindUsingShortName`). It never
   resolves the attribute type — the comment at 289-290 says so. `[Given("the left operand is {int}")]`
   from `Bobcat` therefore matches exactly as Reqnroll's would.
3. **Where the steps go.** `AddToLocalCache` (lines 191-206) puts every step of every admitted
   class into `_mergeData.StepsDefinitionsPerFiles` (line 204), **regardless of**
   `HasReqnrollBindingAttribute` — that flag only decides which of `ReqnrollBindingTypes` /
   `PotentialReqnrollBindingTypes` the class name lands in (lines 198-201), and those two maps
   feed only `GetBindingTypes`, i.e. the create-step quick fix's target list.
4. **Who reads them.** Completion: `CompletionProviders/GherkinStepCompletionProvider.cs:38` →
   `GetStepAccessibleForModule` → `StepsDefinitionsPerFiles` (lines 44-53). Go-to-definition:
   `References/ReqnrollStepDeclarationReference.cs:52` → `AllStepsPerFiles` → the same map.
   Find-usages (`Searchers/ReqnrollSearcherFactory.cs:34,57`) and the undefined-step daemon
   (`Daemon/UnresolvedReferenceHighlight/UnresolvedStepHighlightingDaemonStage.cs:31`) likewise.
   None of them consult the binding flag.
5. **`.feature` file recognition is not package-gated.** The Kotlin side's
   `ReqnrollLanguageSubstitutor.kt` assigns the Gherkin language to any `.feature` whose ancestor
   directory holds a `*proj` or `.cs` file. `IsReqnrollProject` (assembly/package named Reqnroll)
   is used only by the post-build refresher and analytics; `ReqnrollUnitTestProvider.IsSupported`
   gates *test running* only.

**Verdict:** in the same solution, a `partial` fixture's `[Given]`/`[When]`/`[Then]` steps with a
plain string-literal expression should already get completion, navigation, find-usages and
undefined-step diagnostics in Rider with the plugin as shipped — zero Bobcat code. Exclusions that
follow from the same reading: `[Check]` (short name not in the list), `[TableGrammar]`
(class-level), anything in a **referenced assembly** (the assembly path gates on `[Binding]` by
full CLR name — `AssemblyStepDefinitionCache.cs:79` — so the shipped `ClockGrammars` stay
invisible), grammar modules reached only through `[IncludeGrammars]` (the cache follows the
**base class** declaration, lines 271-276, not attributes), and the `Fixture` base class itself
(no source in the solution, so `GetSingleDeclaration()` is null and the walk stops). One side
effect to expect: `MethodNameMismatchPattern` (a HINT) runs on any file with indexed steps, but it
keys on **full CLR name** (`…RecursiveElementProcessor.cs:51`), so it stays silent for Bobcat
attributes today.

**This is a source-reading verification, not a run.** Rider was not launched; the plugin was not
built. It is a hack that costs one keyword per fixture and does not reach the cases the issue's
acceptance criterion names (shipped grammars, `[Check]`), so it does not replace the patch — but
it is the cheapest possible confirmation of the reading, and if it fails in a real Rider the
reading above is wrong somewhere specific.

### The patch — option A, written and verified as far as this machine allows

`docs/rider/0001-bobcat-attributes.patch` (+186/−9 across 5 files; `docs/rider/README.md` is the
cover note with the fork / `gh pr create` commands). It is the sketch below made real, with three
things the sketch did not have:

- **A per-assembly gate on the assembly path.** Scanning every public method of every type in
  every referenced assembly for a binding-less step class would be paid by the BCL too. A type can
  only carry `Bobcat.*` attributes if its assembly is or references `Bobcat`, so
  `CanContainBobcatSteps(IMetadataAssembly)` checks `AssemblyName.Name` and
  `ReferencedAssembliesNames` first. Reqnroll assemblies are untouched: `[Binding]` types are
  admitted exactly as before.
- **The method-naming inspection stays off Bobcat fixtures.** Adding `Bobcat.*` to the name
  arrays would otherwise light `MethodNameMismatchPattern` on every Bobcat step, because it
  expects `GivenTheLeftOperandIs` and Bobcat names steps freely. `GetAttributeStepKind` — that
  daemon's only input — returns `null` for them.
- **Tests.** 39 NUnit cases over the helper, in the plugin's own test project.

**Build status:** the helper **compiles and its tests pass** against the metadata-reader DLLs of
the Rider 2025.1 installed on this machine; both caches **type-check** against the same PSI and
metadata DLLs with only the two expected errors from 2026.2's reshaped `IAssemblyCache`; the
patch applies cleanly on `origin/main`. The plugin was **not** built with the 2026.2 SDK (Gradle +
Java 25 + a multi-GB SDK download, not on this machine) and **not** run in a Rider sandbox.
`docs/rider/README.md` spells out exactly what was and was not done.

**`[Check]` and `[TableGrammar]` under the patch:** `[Check("...")]` becomes a Then on both paths
(full name `Bobcat.CheckAttribute` in assemblies, short name `Check` in source), so the
`[Then]`+`[Check]` stack from the VS Code section is not needed for Rider — though it does no harm
and keeps VS Code happy. `[TableGrammar]` is still invisible: Rider reads method attributes only,
and the class-level expression needs a new concept ("the step's method is `Row`"). Same status as
VS Code; same follow-on.

#### The original sketch, for the record

Touches the helper and the two caches; the quick-fix and daemon keep working unchanged because
they go through the same helper. What was proposed before the patch existed (the patch supersedes
it — read the patch, not this):

```diff
--- a/src/dotnet/ReSharperPlugin.ReqnrollRiderPlugin/Helpers/ReqnrollAttributeHelper.cs
+++ b/src/dotnet/ReSharperPlugin.ReqnrollRiderPlugin/Helpers/ReqnrollAttributeHelper.cs
-    public static readonly ClrTypeName[] GivenAttribute = [new ClrTypeName("Reqnroll.GivenAttribute"), new ClrTypeName("TechTalk.SpecFlow.GivenAttribute")];
-    public static readonly ClrTypeName[] WhenAttribute  = [new ClrTypeName("Reqnroll.WhenAttribute"),  new ClrTypeName("TechTalk.SpecFlow.WhenAttribute")];
-    public static readonly ClrTypeName[] ThenAttribute  = [new ClrTypeName("Reqnroll.ThenAttribute"),  new ClrTypeName("TechTalk.SpecFlow.ThenAttribute")];
+    public static readonly ClrTypeName[] GivenAttribute = [new ClrTypeName("Reqnroll.GivenAttribute"), new ClrTypeName("TechTalk.SpecFlow.GivenAttribute"), new ClrTypeName("Bobcat.GivenAttribute")];
+    public static readonly ClrTypeName[] WhenAttribute  = [new ClrTypeName("Reqnroll.WhenAttribute"),  new ClrTypeName("TechTalk.SpecFlow.WhenAttribute"),  new ClrTypeName("Bobcat.WhenAttribute")];
+    public static readonly ClrTypeName[] ThenAttribute  = [new ClrTypeName("Reqnroll.ThenAttribute"),  new ClrTypeName("TechTalk.SpecFlow.ThenAttribute"),  new ClrTypeName("Bobcat.ThenAttribute"), new ClrTypeName("Bobcat.CheckAttribute")];
+    public const string CheckAttributeShortName = "Check";   // Bobcat's bool-returning Then
@@ IsAttributeForKindUsingShortName
-        if (stepKind == GherkinStepKind.Then && typeShortName.Equals(ThenAttributeShortName))
+        if (stepKind == GherkinStepKind.Then && (typeShortName.Equals(ThenAttributeShortName) || typeShortName.Equals(CheckAttributeShortName)))
+
+    /// True when the attribute is any recognised step attribute, by full name. Used to admit a
+    /// class that has no [Binding] but does declare steps (Bobcat fixtures and grammar modules).
+    public static bool IsAnyStepAttribute(string fullName) =>
+        IsAttributeForKind(GherkinStepKind.Given, fullName) || IsAttributeForKind(GherkinStepKind.When, fullName) || IsAttributeForKind(GherkinStepKind.Then, fullName);
+    public static bool IsAnyStepAttributeShortName(string shortName) =>
+        IsAttributeForKindUsingShortName(GherkinStepKind.Given, shortName) || IsAttributeForKindUsingShortName(GherkinStepKind.When, shortName) || IsAttributeForKindUsingShortName(GherkinStepKind.Then, shortName);

--- a/src/dotnet/ReSharperPlugin.ReqnrollRiderPlugin/Caching/StepsDefinitions/AssemblyStepDefinitions/AssemblyStepDefinitionCache.cs
+++ b/src/dotnet/ReSharperPlugin.ReqnrollRiderPlugin/Caching/StepsDefinitions/AssemblyStepDefinitions/AssemblyStepDefinitionCache.cs
@@ Build
-            if (type.CustomAttributesTypeNames.All(a => !ReqnrollAttributeHelper.IsBindingAttribute(a.FullName.GetText())))
-                continue;
+            var hasBinding = type.CustomAttributesTypeNames.Any(a => ReqnrollAttributeHelper.IsBindingAttribute(a.FullName.GetText()));
+            // Bobcat fixtures and grammar modules carry no [Binding]; admit a type that declares steps.
+            if (!hasBinding && !type.GetMethods().Any(m => m.CustomAttributesTypeNames.Any(a => ReqnrollAttributeHelper.IsAnyStepAttribute(a.FullName.GetText()))))
+                continue;
-            var classCacheEntry = new ReqnrollStepDefinitionCacheClassEntry(type.FullyQualifiedName, true, classScopes);
+            var classCacheEntry = new ReqnrollStepDefinitionCacheClassEntry(type.FullyQualifiedName, hasBinding, classScopes);

--- a/src/dotnet/ReSharperPlugin.ReqnrollRiderPlugin/Caching/StepsDefinitions/ReqnrollStepsDefinitionsCache.cs
+++ b/src/dotnet/ReSharperPlugin.ReqnrollRiderPlugin/Caching/StepsDefinitions/ReqnrollStepsDefinitionsCache.cs
@@ Build
-            if (!hasBindingAttribute && !classDeclaration.IsPartial)
+            if (!hasBindingAttribute && !classDeclaration.IsPartial && !DeclaresSteps(classDeclaration))
                 continue;
+
+    // Cheap, no symbol resolution (this runs while the cache is being built): any method
+    // attribute whose short name is a step attribute.
+    private static bool DeclaresSteps(IClassDeclaration classDeclaration) =>
+        classDeclaration.MethodDeclarations.Any(m => m.Attributes.Any(a => ReqnrollAttributeHelper.IsAnyStepAttributeShortName(a.Name.ShortName)));
```

Plus a bump of `ReqnrollStepsDefinitionsCache.VersionInt` (cache format is unchanged but the
population rule is, and a stale persisted cache would otherwise keep the old answer), and a case
in `ReSharperPlugin.ReqnrollRiderPlugin.Tests/Caching/StepsDefinitions/AssemblyStepDefinitions/AssemblyStepDefinitionCacheTests.cs`
for a `[Binding]`-less type with `Bobcat.GivenAttribute`.

**Size, estimated then vs. actual now:** the sketch guessed ~40 lines across 3 files + a test; the
patch is +186/−9 across 5 files, of which 105 lines are the test file and 3 the changelog — the
production change is ~80 lines. The code took an afternoon as predicted; the build loop is the
part still owed (see build status above). Risk is low: the change only *admits* more types, and
the helper's tests pin Reqnroll/SpecFlow behaviour.

Known gaps after option A: `[TableGrammar]` (class-level expression; Rider reads only method
attributes, so it needs a new concept — "the step's method is `Row`"), and the
create-missing-step quick fix, which would still generate `Reqnroll.*` attributes into a Bobcat
fixture and offers only `[Binding]` classes as targets. Both are follow-ons, not blockers for
completion and navigation.

### Option B — make the names configurable

The principled version: a settings-backed list of binding / step attribute CLR names instead of
(or on top of) hardcoded `Bobcat.*`. It is a materially bigger change than A because
`ReqnrollAttributeHelper` is a static class of `static readonly` arrays read from cache-build code
that has no component context: the helper would become a `[ShellComponent]` (or the arrays would
be rebuilt from a `SettingsKey` on change), the two caches would take it by injection, the
settings need a `SettingsKey` class on the .NET side and an options page in the Kotlin front-end,
and `VersionInt` must fold the setting into the cache key. Estimate **150–300 lines across
.NET and Kotlin, plus UI**; two to three days including the plugin build loop.

Recommendation: **submit A** — it is written (`docs/rider/`), general enough ("admit any class
that declares steps" helps every wrapper library, and the `Bobcat.*` names are four entries), and
its PR body already offers B if the maintainers would rather not carry a third framework's names.
Fork as "Bobcat for Rider" only if the PR stalls — per the issue's own plan. What remains before
submitting is the 2026.2 build and a look in the sandbox; what remains after is `[TableGrammar]`
and the quick fix, both follow-ons.

## How this was verified

The extension's language service is an npm package with a Node entry point, so it can be run
over real files without launching VS Code. The harness used here (kept out of the repo; it is
five lines of wiring):

```js
import { ExpressionBuilder } from '@cucumber/language-service'
import { WasmParserAdapter } from '@cucumber/language-service/wasm'
const adapter = new WasmParserAdapter('node_modules/@cucumber/language-service/dist')
await adapter.init()
const result = new ExpressionBuilder(adapter).build(
  files.map((f) => ({ languageName: 'c_sharp', uri: 'file://' + f, content: readFileSync(f, 'utf8') })),
  [{ name: 'decimal', regexp: '-?[\\d.]+' }])
// result.expressionLinks — one per matched attribute, with file + line
// result.errors          — expressions the parser rejected
```

Run over every fixture and grammar file under `src/` and `samples/`, and over a synthetic file
holding one of each attribute shape in the table above. The numbers in this document come from
that run. Nothing about Rider was executed; that section is a reading of the plugin's source.
