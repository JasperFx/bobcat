# Runner adapters and specification identity

### Runner adapters (`Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit`, `Bobcat.MSTest`)

- **`[BobcatSpec]` is the one-attribute projected spec, and it is three facts rather than one
  (issue #403).** `Bobcat.Xunit.BobcatSpecAttribute` IS the `[Fact]`, opens the scenario recording
  `[BobcatScenario]` opens, and carries the `[BobcatSlice]` settings — so
  `[BobcatSpec(typeof(ConfirmAppointment))]` replaces three attributes, and **BOBCAT028 becomes
  impossible by construction** because the attribute that claims the slice is the attribute that
  opens the recording.
  - **It can be both a Fact and a bracket because xUnit v3 collects hooks by the
    `IBeforeAfterTestAttribute` INTERFACE**, not only from the `BeforeAfterTestAttribute` base
    class. The two bases are siblings — both derive straight from `Attribute` — so inheriting from
    both was never an option, and this is the only shape that collapses the pair. Undocumented
    enough that `BobcatSpecAttributeTests` pins it against the real runner. `XunitScenarioBracket`
    exists so the two attributes do not each own a copy of the bracket, the gathered-wrong rethrow
    included.
  - **The `[CallerFilePath]`/`[CallerLineNumber]` pair is re-declared and forwarded, and that is
    load-bearing.** `FactAttribute`'s only constructor takes it and the compiler fills it at the
    call site it SEES, so a subclass calling `base()` without re-declaring them hands over its own
    file and the line of that `base` call — every test in a suite reporting one source location,
    with IDE test navigation landing on the attribute. Measured both ways before being written.
    The attribute is **unsealed** (`[PostgresFact]` is the shape to expect) and a subclass inherits
    the same obligation.
  - **The generator needed teaching in three separate places, because it matches by simple name and
    never sees a base type**: `IsTestMethod` ("Fact"/"Theory"/"Test"/"TestCase"/**"BobcatSpec"**),
    `HasScenarioAttribute` (now `ScenarioOpeningAttributes`), and `sliceTags`. Missing any one
    fails differently — the method is skipped entirely, or BOBCAT028 fires at a suite that records
    perfectly well, or the slice goes unbound.
  - **`sliceTags` reads BOTH attributes rather than preferring one**, and an unnamed `typeof(…)`
    argument is the positional `SliceType`. They are the same settings under two spellings, so a
    method carrying both is one binding stated twice and the existing BOBCAT023 rule already
    answers a disagreement: refuse, because one is wrong and no silent winner is right. Preferring
    one would make the loser's slice vanish without a word. BOBCAT024 now names the attribute it
    fired on.
  - **TUnit's equivalent is deliberately a follow-up** (the issue says so): this subclasses
    `Xunit.FactAttribute`. `[BobcatScenario]` stays for the class-level opt-in and for suites
    already written against it. `Bobcat.Xunit.Samples/Specs/OneAttributeSpecs.cs` keeps one class
    in each form, which is what makes
    `ProjectedSpecIdentityEndToEndTests.a_projected_listing_covers_every_specification…` a real guard:
    the generator recognises the attribute by name and xUnit by base class, two independent
    mechanisms, and that test asserts the two counts agree.
- **A projected run's bracket is per REQUEST, not per process (issue #402).**
  `MarkerStepRun.OpenRun(mode)` / `CloseRun()` are the seam; `Bobcat.Xunit` ships an
  `ITestSessionLifetimeHandler` (`BobcatSessionLifetime`) that drives them. Two run requests in one
  live process now publish two `run_started`, two `RunId`s and two `run_finished`, each command's
  scenarios under its own run — which is #393's requirement in as many words, and the absence of
  `run_finished` was #195's definition of a wedged run.
  - **A test SESSION is a run request, and that is measured on MTP 1.9.1.** Three
    `testing/runTests` requests into one live process fire the handler three times with three
    distinct `SessionUid`s, each properly bracketed; a one-shot direct run fires it once; and
    **discovery opens no session at all**, which is what keeps `--list-tests` from putting an empty
    card on the board.
  - **Registration is half C# and half MSBuild.** The platform generates a
    `SelfRegisteredExtensions` class calling `AddExtensions` on every type a
    `TestingPlatformBuilderHook` item names, so `Bobcat.Xunit` ships
    `buildTransitive/Bobcat.Xunit.props` — and **in-repo projects declare the item themselves**,
    because a `ProjectReference` takes no build assets. **Except the samples (bobcat#484):**
    `samples/Directory.Build.props` `<Import>`s `Bobcat.Generators.props` and `Bobcat.Xunit.props`
    straight from `src/*/buildTransitive/` for every `Tests` project, so a sample is configured
    exactly like a package consumer and no `Tests.csproj` restates either setting.
  - **A missing interceptor opt-in can no longer pass vacuously (bobcat#484).** Without
    `Bobcat.Generated` in `InterceptorsNamespaces` the generator emits no interceptors and reports
    **BOBCAT037** (error) once, at the first call site, read from the same parse option the compiler
    enforces (it was a CS9137 per call site before). At run time, a `SpecAssert` wrong with a scenario
    open and **no step** open (an un-intercepted helper, or no generator at all) goes on
    `Recording.SteplessWrongs` and fails the scenario through `GatheredFailures`; it used to be
    dropped. A *passing* check outside a step stays silent.
  - **`Bobcat.Xunit` takes a `Microsoft.Testing.Platform` reference; `Bobcat.TUnit` deliberately
    does NOT.** `xunit.v3.extensibility.core` depends only on `xunit.v3.common`, so this is the
    first platform reference either adapter has had — and it costs an xUnit consumer nothing, since
    such a consumer already resolves the platform through `xunit.v3` at the same pinned 2.5.1.
    `TUnit.Core` depends on no test platform at all, which keeps that package free of a platform
    choice that belongs to the consumer's own `TUnit.Engine`. **It gets its per-request bracket
    from TUnit.Core instead (bobcat#470):** `BobcatScenarioAttribute` is also an
    `ILastTestInTestSessionEventReceiver` and closes the run once the session's last scenario has
    ended (an open-scenario count, because parallel tests give no ordering promise), so two warm
    commands into one live TUnit host are two runs. The reasoning lives in `Bobcat.TUnit.csproj`.
  - **`Bobcat.TUnit` calls `context.AddAsyncLocalValues()` in `OnTestStart` (bobcat#470).** TUnit
    1.74.0 does not flow AsyncLocal values set in a receiver into the test or the end receiver
    unless asked. Without it the scenario opened, `OnTestEnd` found no `ScenarioRecorder.Current`,
    and **no verdict was ever published** (only `scenario_started`). Nothing in this repository could
    run a real TUnit host before #470, so it went unseen; `TUnitResidentSuiteTests` now would catch it.
  - **`Bobcat.NUnit` and `Bobcat.MSTest` (issues #385/#386) follow `Bobcat.TUnit`: no platform
    reference.** Their *test projects* run on NUnit3TestAdapter 5.2.0 and MSTest.TestAdapter 3.11.1,
    the last releases built on MTP 1.9.x (6.0 / 4.0 move to 2.x) — `src/Directory.Packages.props`
    says so. Two decisions worth not re-deriving:
    - **MSTest's bracket is a `[GlobalTestInitialize]`/`[GlobalTestCleanup]` pair (MSTest 3.10+),
      shipped as SOURCE in `buildTransitive/BobcatMSTestHooks.cs`**, and `[BobcatScenario]` there is
      a plain marker the hooks look for. MSTest only finds global hooks in the test assembly, and
      the alternatives the issue listed both fail its "add one attribute" rule (a
      `TestMethodAttribute` subclass replaces `[TestMethod]`; `[TestCleanup]` needs a base class).
      **`Bobcat.MSTest` references no MSTest assembly**: MSTest 4 renamed them
      (`Microsoft.VisualStudio.TestPlatform.TestFramework` → `MSTest.TestFramework`) and turned
      `TestMethodAttribute.Execute` into `ExecuteAsync`, so a type compiled against one major does
      not load under the other. The hook source compiles against the consumer's MSTest and passes
      plain values (the outcome by *name*). Verified as a package consumer on MSTest 4.5.1 out of
      tree, since this repo cannot run an MTP 2.x host.
    - **Both test projects prove the failing cases in a child process** (`Probes.cs`): a probe
      class kept out of the normal run (`[Explicit]` for NUnit, a `ConditionBaseAttribute` for
      MSTest) is re-run by the real host with a filter, and a `ScenarioCompleted` listener writes
      what was published. That is how "the outcome is populated in AfterTest / GlobalTestCleanup"
      and "a throw from there fails the test" are tested rather than assumed. Note `SpecAssert`
      records onto the **step in progress**; a wrong with no step open goes on `SteplessWrongs`
      (above).
    - **Run by identity refuses both** (`SpecFilterArguments`), like TUnit — but unlike TUnit their
      filter spelling *could* be verified here, since both run on MTP 1.9. That is the follow-up.
  - **`ProcessExit` stays as the backstop and now drains unconditionally.** `CloseRun` is
    idempotent, so a bracket the session hook already closed is found closed — but the old code
    returned early when it had nothing to close, which after this refactor would have let the last
    `run_finished` of every warm process die in the publisher's channel. **`CloseRun` keeps the
    sink** (disposing it would stop the pump for the next request) and resets `_info`/counts so the
    next bracket mints a fresh `RunId` with that request's own tallies; only process exit disposes.
    Ordering needs no flush — one bounded channel, drained FIFO.
  - **The command id is read per request** (`OpenRun` re-runs `MonitorRunInfo.Discover`), while the
    sink is resolved once per process (`_sinkResolved`) so no command pays for a console handshake.
    **For a warm out-of-process child the variable still cannot change**, and that is the finding
    this uncovered: a child's environment is fixed at launch, and MTP 1.9.1 has no per-request
    metadata slot — the `runId` on `testing/runTests` is the client's own and is **not** the
    `SessionUid` the handler receives (measured; unrelated GUIDs). A file whose path is fixed at
    launch, the way `BOBCAT_LIST_SPECS` works in the other direction, is the answer —
    `BOBCAT_RUN_COMMAND_FILE`, under the resident runner's warm suite.

### Listing and running by specification identity (issue #391)

A monitor only ever names **identities** — `{Feature}/{Scenario}` — and never a test framework's
own filter. Three pure pieces in core plus one artifact:

- **`SpecIdentity`** names the string every consumer keys on (the retry budget's test id,
  `DeclaredSteps`'s key, `scenario_finished`'s uid, the ledger, the timing report, the generator's
  descriptor, `WorkPlan`'s key). Comparison is **ordinal and exact**: this is a machine identity,
  not a search, so folding case would let two scenarios differing only in case collide into one —
  silently running the wrong spec, which is worse than a miss, because a miss is reported. Nothing
  offers to *parse* an identity: a feature title may contain a `/`, so the pieces are read off the
  model, never out of the string.
- **`SpecSelection`** is the set a run was asked for. **Empty means everything**, so a run path
  takes one unconditionally; `NarrowsAnything` is how a caller tells "nothing in particular" from
  "asked, and it covers the suite" — the distinction MTP forces, since it ignores a subset
  parameter it does not understand and runs the whole suite (`GuardAgainstAnUnfilteredRun`'s
  lesson). `NotIn(known)` is how a runner rejects a foreign identity **by name, before running**,
  rather than running a narrowed suite that matched nothing and exiting 0.
- **`SpecManifest`** is what a suite says it specifies, written to the path `BOBCAT_LIST_SPECS`
  names and **only when something asked**. A file, not stdout, because the listing has to cross a
  process boundary — a resident runner is not the suite, and for a projected suite it *cannot* be.
  Not `--list-tests` either: that prints display names, not identities. Writing it **can never
  fail the suite** — same invariant as the monitor probe. `Framework` (xunit/tunit/nunit/mstest)
  settles the filter spelling; `Lane` is always `projected` now. It also carries the suite's
  recovery hints (#469, see Resilience).
- **`SpecFilterArguments.For(manifest, selection)`** is the translation: a projected suite's
  platform uid is its framework's own (assembly + class + method + arguments), so the identity
  becomes `--filter-method Ns.Class.method`. It **refuses rather than guesses** — an unknown
  framework, an unbound entry, or an identity the manifest lacks all throw. **NUnit and MSTest are
  refused**: they run on MTP 1.9 here, so verifying theirs is the follow-up. An unverified filter is
  exactly the run that looks filtered and is not.
  - **TUnit is `--treenode-filter "/*/<Namespace>/<Class>/<Method>"` (bobcat#470)**, measured
    against the real `Bobcat.TUnit.Tests` host (`TUnitTreeNodeFilterTests`): the namespace is one
    segment with its dots kept, a nested class is `Outer+Inner`, segments match whole words, a
    data-driven method's path selects every case, and a filter matching nothing exits 8. **The
    option is accepted once and ORs only within a segment** (`/*/Ns/(A)|(B)/(x)|(y)`): a whole-path
    alternative is a parse error, and an unparenthesised `|` between two paths silently selected
    the wrong tests. So several identities become one filter only when they are exactly the cross
    product of their namespaces × classes × methods (one class, or one method name across classes);
    anything else is **refused** rather than over-selected. A class in the global namespace is
    refused too (that spelling is unmeasured). Warm runs by uid and has neither limit.

**The manifest is written from the generated module initializer**, which calls
`DeclaredSteps.Bind(uid, class, method)` and then `SpecManifest.WriteIfRequested`, because that
initializer is the only code Bobcat owns in that process and the only moment sure to run —
discovery loads the assembly, so `--list-tests` reaches it without executing a test. The binding is
**recorded, not re-derived**: the identity is a one-way function of the names and an explicit
`[BobcatFeature("…")]` title shares nothing with its class name. `MarkerCommentSpecs.FrameworkOf`
type-probes for each adapter's `BobcatScenarioAttribute`, the same pattern as the
`EventModelSliceDescriptor` gate.

- **A binding is emitted on a wider rule than `DeclaredSteps.Register`**, and the difference is
  load-bearing. A projected spec declares its steps by marker comments, by `[BobcatStep]`
  interceptors, **or by grammar calls** — and only the first reaches `Register` (an unmarked test is
  deliberately not a declared-steps entry). Keyed on that set, the first cut of this listed **8 of
  `Bobcat.Xunit.Samples`' 41 specifications** while the other 33 rendered and published verdicts
  perfectly well. What makes a test a specification is that `[BobcatScenario]` records it, so
  `Bind` keys off `OpensRecording` and `HasAnything` replaced `HasSteps` as the gate on emitting the
  initializer at all.

- **Each data row is its own specification (bobcat#569).** A row's identity is the method's plus
  `SpecRows.Describe`'s row part: `(name: value, …)` (invariant culture, strings verbatim, enum by
  name, line breaks escaped) or `[label]` (xUnit's `TestLabel`, which `[BobcatExample("label", …)]`
  sets; TUnit/NUnit/MSTest carry none). Every adapter passes the row's arguments to
  `MarkerStepRun.BeginScenario` (xUnit `IXunitTest.TestMethodArguments`, TUnit
  `TestDetails.TestMethodArguments`, NUnit `ITest.Arguments`, MSTest `TestContext.TestData` through
  the hook source). `Recording.Uid` is the row's; `ParentUid` / `SpecUid` is the method's, and the
  declared and planned steps are looked up by `SpecUid` (one body, one registration). The Event Model
  keeps one specification per method; rows ride `ParentSpecId` on `scenario_started`/`finished`.
  `[BobcatTheory]` is `[BobcatSpec]` for a theory (the generator's `TheoryAttributeName`, read
  everywhere `SpecAttributeName` is). The generator lists **constant** rows (`SpecRowConstants`:
  InlineData/BobcatExample/Arguments/TestCase/DataRow) through `DeclaredSteps.Bind(row, class, method,
  parent)`, only when the constant's text cannot change in the runner's conversion; BOBCAT036 covers
  duplicate rows. `MarkerSpecNaming.RowArguments`/`RowLabel` is the generator's copy, pinned by
  `SpecRowIdentityTests`. A row identity is refused by `SpecFilterArguments` (a method filter runs
  every row) and skipped by `SpecUidJoin`.
- **Duplicate identities and the explicit scenario name (bobcat#489).** Two projected specs
  resolving to one identity is **BOBCAT036** (error), computed in
  `MarkerCommentSpecs.DuplicateIdentities` over `IdentityOf`, the same string `Emit` writes, so it
  cannot disagree with the runtime. `[BobcatSpec(Name = "…")]` states the scenario title verbatim:
  the generator reads it by name (`ScenarioTitleOf`, used by every identity site: marker specs, the
  step interceptors' `UidOf`, `SpecificationManifest`, the Event Model), the runtime through core's
  `IExplicitScenarioName` (`MarkerSpecNaming.ScenarioTitle(MethodInfo)` / `(Type, name)`). The
  run-time guard: `DeclaredSteps.Bind` keeps the FIRST binding and records the rest
  (`DeclaredSteps.Duplicates`); the manifest carries them as `Duplicates`, `WriteIfRequested`
  prints them on stderr and **still writes** (never fail the suite), and `SpecFilterArguments`
  refuses a duplicated identity by name.

`Bobcat.Xunit.Tests/SpecIdentity/ProjectedSpecIdentityEndToEndTests` pins it against the real
`Bobcat.Xunit.Samples` host — listed and then run by identity.
