# Bobcat with xUnit.net

`Bobcat.Xunit` lets an **xUnit v3** suite you already have publish itself as Bobcat specifications  -- 
the tests keep running on xUnit, and Bobcat renders, reports and tracks them as scenarios.

```bash
dotnet add package Bobcat.Xunit
```

Nothing is rewritten, nothing moves, and the tests keep their own runner. For writing new
specifications, see [Writing Specifications](marker-steps.md).

## Wire it up

Name the feature the test class belongs to:

```csharp
using Bobcat;

[BobcatFeature("Async daemon")]
public class DaemonSpecs
{
    [Fact]
    public async Task the_daemon_catches_up()
    {
        // Given events are published
        await PublishEvents(10);

        // When the daemon runs
        await RunDaemon();

        // Then the projection is caught up
        (await Projection()).Count.ShouldBe(10);
    }
}
```

That is all it takes. The package applies an assembly-level xUnit hook,
`[assembly: RecordBobcatFeatures]`, that opens a scenario around every test in a `[BobcatFeature]`
class and closes it with **the verdict xUnit reported** -- a failing test is published as a failure,
and a skipped one is withdrawn rather than reported as anything at all. Opt out with
`<BobcatRecordFeatures>false</BobcatRecordFeatures>`. A `ProjectReference` to `Bobcat.Xunit`
doesn't consume the package's build assets, so such a project imports the package's props instead --
see [Referencing Bobcat by ProjectReference](getting-started.md#referencing-bobcat-by-projectreference).

`[BobcatScenario]` on a class or a single method still works, for a suite that would rather opt in
test by test, and `[BobcatSpec(typeof(SomeCommand))]` is a `[Fact]` that also binds the test to an
Event Model slice and opens its recording -- see [Writing Specifications](marker-steps.md#bobcatspec).

The scenario's title is the method name, so the method name is user-facing text here in a way it is
not in an ordinary test.

## What it renders

With the adapter in place, a projected suite does rather more than publish a verdict. All of it is
covered in [Writing Specifications](marker-steps.md); the short list, so you know what to look
for:

| | |
|---|---|
| [A rendered specification on the console](marker-steps.md#reading-the-specification-the-run-produced) | on by default in a terminal with no console listening; `BOBCAT_SPEC_CONSOLE` overrides in both directions, and `BOBCAT_SPEC_PREVIEW=1 ./MySpecs --list-tests` previews without running |
| [Expected/actual cells](marker-steps.md#checks-that-gather-instead-of-throwing) | `SpecAssert.Check(name, actual, expected)` records instead of throwing, so a specification shows *every* disagreement rather than only its first. The adapter turns the gathered wrongs into the test's verdict at the end |
| [Tables and sets](marker-steps.md#tables-and-sets-from-a-c-test) | a table literal in the test -- pipe-delimited text that `StepTable` reads by an implicit conversion -- rendered as one grid |
| [Projected assertions](marker-steps.md#projecting-the-assertions-you-already-wrote-opt-in) | with `<BobcatProjectAssertions>true</BobcatProjectAssertions>`, ordinary statement-level Shouldly calls render as steps and a *run* of them is all evaluated before the next action |
| [One specification per theory row](spec-identities.md#each-data-row-is-its-own-specification) | `[BobcatTheory]` is `[BobcatSpec]` for a `[Theory]`; each row records as `Feature/Scenario (amount: 100)`, or `Feature/Scenario [label]` with `[BobcatExample("label", …)]` |
| [Theory examples as a table](marker-steps.md#bobcatexamples) | `[BobcatExamples("""\| amount \| tier \|…""")]` gives one specification per row, its columns and cells checked at compile time (BOBCAT032, BOBCAT042) |
| [Not yet passing](marker-steps.md#acceptance) | `[BobcatSpec(…, Lifecycle = Lifecycle.Acceptance)]` records a failure in full as `acceptance: failing` and xUnit reports the test skipped, so the build stays green; `Pending = true` is a stub that does not run at all |
| [Step logs](marker-steps.md#step-logs) and [attachments](marker-steps.md#attachments) | `SpecLog.Write` and `SpecAttachment` hang evidence on the step in progress; both reach xUnit's own test output and attachments |
| [Listing and running one specification](spec-identities.md#listing-and-running-by-identity) | `BOBCAT_LIST_SPECS` writes what the suite specifies; an identity is translated to `--filter-method Ns.Class.method`, which is what [the resident runner](resident-runner.md) drives |

One step-attribute family -- `[Given]`, `[When]`, `[Then]`, `[Check]` and the keywordless `[Step]` --
is intercepted at the call site when a test calls the method. `[BobcatStep]` is an older spelling of
the same thing.

::: tip The sample corpus
[`src/Bobcat.Xunit.Samples`](https://github.com/JasperFx/bobcat/tree/main/src/Bobcat.Xunit.Samples)
is a corpus of ordinary xUnit v3 specifications -- sentences, facts, output parameters, tables, sets,
decision tables and narrated tests. Many of them **fail on purpose**, because the samples exist to
show what each outcome looks like.
:::

## Use this package rather than writing forty lines

Bobcat deliberately shipped no adapter at first, on the theory that forty lines were cheaper than
guessing at a runner. That was wrong, and the reason is worth stating because the failure is
invisible: those forty lines carry four things only Bobcat knows, and getting any of them wrong
leaves a green suite looking exactly like a correct one.

Marten's hand-rolled copy got all four wrong:

- It never set the verdict, so **every scenario it ever published was a clean pass** -- including
  the failing ones.
- It minted its own run id and dropped `BOBCAT_RUN_TAG`, so its evidence could not be attributed to
  whatever asked for the run.
- It published a `RunStarted` it might not own, and never a `RunFinished`.
- Its interceptor opt-in was a csproj line a sibling project also needed -- and forgetting it was a
  build failure in a generated file (now **BOBCAT037**, which the package's props make impossible).

## Moving to xUnit v3 4.x {#moving-to-xunit-4}

<!-- JEREMY: an intro sentence or two on why Bobcat moved (bobcat#470), in your voice. -->

`Bobcat.Xunit` builds against xunit.v3 4.0.2 and Microsoft.Testing.Platform 2.5.1 (TUnit.Engine
1.74.0 runs on the same platform) since Bobcat 0.60.0; [Version Compatibility](/compatibility) has the
full table. If only one project in your repository moves, see
[Adopting Bobcat in one project of a larger repository](/compatibility#partial-adoption). What
changes in a consuming test project:

- **Move `xunit.v3` to 4.x.** The platform and xUnit move together; a 3.x `xunit.v3` beside
  Microsoft.Testing.Platform 2.x fails at run time.
- **Opt `dotnet test` into the platform's own mode in `global.json`.** Microsoft.Testing.Platform 2.x
  refuses the VSTest bridge on the .NET 10 SDK:

  ```json
  {
    "test": {
      "runner": "Microsoft.Testing.Platform"
    }
  }
  ```

- **Name the project or solution with an option, and pass test-host options directly.**
  `dotnet test MySpecs -- --filter-class "*Wallet*"` becomes
  `dotnet test --project MySpecs --filter-class "*Wallet*"` (`--solution` for a solution).
- **`[assembly: CollectionBehavior(DisableTestParallelization = true)]` is now a compile error.**
  Use `[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]`.
- **A `TestCaseOrderer` now only orders the cases within one method.** xUnit 4 orders collection,
  then class, then method, then case; to order the methods of a class, implement
  `ITestMethodOrderer` and use `[TestMethodOrderer]`.
- **Set `IsTestingPlatformApplication=false` on a helper project that references the platform but
  is not a test host.** The platform's `dotnet test` mode collects every project where it is true
  and ignores `IsTestProject=false`.

## xUnit v2 is not supported, and is not waiting for an adapter

This is a structural limit rather than a backlog item. v2's `BeforeAfterTestAttribute` is
`Before(MethodInfo)` / `After(MethodInfo)` with no test context anywhere -- **there is no verdict to
read at that point at all**. Reporting one would mean replacing the test framework rather than
hooking it.

v3 and TUnit both hand the result over at the end of a test, which is exactly what makes their
adapters small.

## Turning the test into a readable specification

The attribute publishes the test. Marker comments and decorated step helpers are what make it
*read* as a specification -- a `// Given …` comment becomes a step, and a decorated shared helper
declares its step once for every test that calls it.

That is the larger subject: [Writing Specifications](marker-steps.md).

## See also

- [Bobcat with TUnit](tunit.md) -- the same thing, other runner
- [Writing Specifications](marker-steps.md) -- step helpers, marker comments, `SpecAssert` and the scenario vocabularies
