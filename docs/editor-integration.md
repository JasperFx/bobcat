# Editor integration — step completion and go-to-definition (issue #109)

Status, 2026-08-21:

| Editor | Status | What it costs |
|---|---|---|
| **VS Code** | Works today, zero Bobcat code | Install the official Cucumber extension, commit three settings (`.vscode/settings.json` in this repo is the sample) |
| **Rider** | Not yet | An upstream change to `reqnroll/Reqnroll.Rider` — proposal and diff sketch below; **not opened**, Jeremy's call |

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
read from the code.

One experiment worth a minute before any PR: since the source path admits a `partial` class, and
steps from such a class go into `StepsDefinitionsPerFiles` regardless of its binding status,
declaring a fixture `public partial class CalculatorFixture : Fixture` may already light up
completion and navigation for *source* fixtures in Rider with the plugin as shipped. That would
not cover `[Check]` or the shipped grammars, and it is a hack, not the answer — but it is a cheap
way to confirm the reading above.

### Proposed change — option A, add the Bobcat names (recommend opening this)

Touches the helper and the two caches; the quick-fix and daemon keep working unchanged because
they go through the same helper. Sketch (not a tested diff):

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

**Size:** roughly 40 changed lines across 3 files + a test. The code is an afternoon; the cost
is the build — Gradle + the Rider SDK download + a Rider sandbox to verify in — call it a day
for someone who has not built a Rider plugin before. Risk is low: the plugin's own tests cover
the assembly cache, and the change only *admits* more types.

Known gaps after option A: `[TableGrammar]` (class-level expression; Rider reads only method
attributes, so it needs a new concept — "the step's method is `Row`"), and the
create-missing-step quick fix, which would still generate `Reqnroll.*` attributes into a Bobcat
fixture. Both are follow-ons, not blockers for completion and navigation.

### Option B — make the names configurable

The principled version: a settings-backed list of binding / step attribute CLR names instead of
(or on top of) hardcoded `Bobcat.*`. It is a materially bigger change than A because
`ReqnrollAttributeHelper` is a static class of `static readonly` arrays read from cache-build code
that has no component context: the helper would become a `[ShellComponent]` (or the arrays would
be rebuilt from a `SettingsKey` on change), the two caches would take it by injection, the
settings need a `SettingsKey` class on the .NET side and an options page in the Kotlin front-end,
and `VersionInt` must fold the setting into the cache key. Estimate **150–300 lines across
.NET and Kotlin, plus UI**; two to three days including the plugin build loop.

Recommendation: **open A** (it is general enough — "admit any class that declares steps" helps
every wrapper library, and the `Bobcat.*` names are three lines), offer B in the PR description
if the maintainers would rather not carry a third framework's names, and fork as "Bobcat for
Rider" only if the PR stalls — per the issue's own plan.

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
