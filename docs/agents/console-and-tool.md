# bobcat ui, the bobcat tool, import-event-model, the resident runner, MIT and Stoat

**The console that receives all of this is back in this repository as `bobcat ui` (#524).** It
left on 2026-09-18 (3ee3db9, "Carve the console out of Bobcat") for
[Stoat](https://github.com/JasperFx/stoat) and came back in #525: the Event Model canvas and the run
board belong in the free tool, because the Critter Stack needs that visualization without a
licence. What came back:

- **`src/Bobcat.Ui`** — the host: `/api/ingest`, `/api/runs`, `/api/event-model`, the exports and
  ejects, the SignalR relay, the run registry and its archive (`~/.bobcat/monitor/runs`), the
  resident-runner wire, the idle ceiling, and `/api/agent/*` for the CLI queries. It keeps
  **Wolverine + Wolverine.HTTP + Wolverine.SignalR**, as in both earlier homes. **Only the `bobcat`
  tool references it, never a Bobcat library**, so `WolverineFx.Bobcat → Bobcat` stays the only edge
  between the repositories and there is no package cycle.
- **`src/Bobcat.Ui.FrontEnd`** — the Vue SPA, embedded under `EmbedFrontend=true` (the CritterWatch
  pattern); `ui-frontend.yml` and `./build.sh UiSmoke` are its gates.
- **`Bobcat.Ui.Tests` / `Bobcat.Ui.Specs`** — `ContractRoundTripTests` and `TypeScriptContractTests`
  keep the receiver's copy of the contracts honest against this repository's publisher, so a wire
  change that breaks the console now fails here. The SPA's `src/messages/monitor-events.ts`,
  `relayRoutes.ts` and `results-file.ts` are generated and gitignored (#604): `./build.sh UiContracts`, or the frontend's
  npm pre-hooks after one `dotnet build`. Never commit them; `relayToStore.ts` is hand-written. `Bobcat.Tests/Monitoring/FakeMonitorHost` is still
  how core is tested against real HTTP without the console.
- **The agent-facing queries are CLI commands, not MCP tools** — JSON on stdout over a running
  `bobcat ui` (`docs/agent-queries.md`). MCP stays Stoat's.

Persistence is only what the console keeps while it runs; nothing new. **Stoat still binds 5525
until its own half of #524 ships** (step 5, tracked in the stoat repository), so today a person runs
one or the other on that port, or moves one with `--port` and lists both in `BOBCAT_MONITOR_URL`.
Decisions of record: `docs/monitor-design.md` (the wire and the receiving-side folds) and
`docs/bobcat-ui.md`.

**The `bobcat` tool is `src/Bobcat.Console/`**: `run` (the default — a bare `bobcat` runs the spec
project found from the current directory), `preview`, `pick` and `watch` over a spec project,
`import-event-model` and `resident` (`docs/bobcat-tool.md`). Registration is explicit, never a
scan (issue #369). It carries the free, no-server half of the toolset — convert an eventmodelers.ai
board export into C#, optionally pushing the assembled model at a console's
`PUT /api/event-model`; and (issue #399) keep a suite whose process Bobcat does not own available
to a monitor, which has to ship from here because the console references nothing in this repository
and so cannot be handed a class to host.

- **`import-event-model` writes C#, not YAML (issue #405).** `CSharpModelWriter` emits two files:
  `<Model>Stubs.cs`, one **field-less** record per command, event, aggregate, message and read
  model the board named, and `<Model>.cs`, one `EventModelDefinition` declaring the slices through
  JasperFx.Events 2.81's fluent API. `EmlangReader` and `EmlangImport` are untouched — only the
  writer at the end of the pipe changed — and the command's **curated arm is gone**: a curated
  file is refused with a sentence pointing at `EventModelDefinition`, which is more use than the
  "unrecognized" it would otherwise fall through to.

- **`--specs` is gone (#744).** Writing WolverineFx.Bobcat specifications and their spec project from a board
  (`EmlangSpecWriter`, `SpecProjectWriter`, and the `--specs`, `--specs-out`, `--specs-namespace`,
  `--app-project`, `--store`, `--bobcat-version` and `--wolverine-version` flags) moved to a Wolverine-side
  command beside `wolverine scaffold`, because it writes Wolverine specs and checks the app's Wolverine setup.
  What stays: `ProjectedSpecNaming.ExampleMethodName` / `ExampleScenarioTitle` (the scenario names the import
  gives the model's examples, which the spec writer must agree with), `CSharpModelWriter.StubAdditions` (what
  a spec writer needs of the stubs; null here), and the spec-related callout codes.

- **A re-import without `--force` is additive (`ImportMerge` + `ImportFileWriter`, 2026-10-12).** The
  command used to keep every existing file whole, so a new slice got stub and spec files but no
  declaration (and `wolverine scaffold` never saw it), and a new type on an old slice was silently
  dropped. Now an existing file is merged into: missing slice declarations at the end of the chapter
  definition's `Configure` (written via `CSharpModelWriter.SliceDeclaration` against the file's own
  `InChapter`/`InDomain`/`ForAggregate`, not a fresh import's), missing stub types at the end of the
  namespace. (Missing specs at the end of the `[BobcatFeature]` class and missing `global using`s left with
  the spec writer in #744.) Rules that matter:
  - **Never change or remove anything that exists.** A type whose board members it lacks or types
    differently is `type-differs-from-board`. Members the code ADDED are not a difference.
  - **A new role on an existing slice is a reopening statement, never an edit (import-role-additions,
    0.60.3).** For each `Emits`/`Publishes`/`On`(`From`)/`Reads`/`Produces`/`Projects` the board gives a
    declared slice and NO statement in the project says, one `model.Slice("X").Emits<E>()…;` is appended
    to the end of the chapter definition's `Configure` (several roles = one statement, a call per line):
    `merged-slice-roles`, with `ImportCallout.Roles`. This works because JasperFx's
    `EventModelBuilder.Slice(name)` always adds a NEW slice builder, and `EventModelDescriptor.Merge`
    (via `GroupByName`, which every host path uses) folds same-name slices, same-rung lists unioned, in
    one definition or across definitions. Verified 2026-10-12 against JasperFx.Events 2.85.0 and
    origin/main 2.85.1; nothing new was needed in JasperFx. Rules that matter:
    - **The reopen repeats an explicit `.InChapter`/`.InDomain` of the declaration** when the default in
      force at the end of `Configure` is another. Without it the reopen takes the definition's chapter
      and the merge records a `SourceDisagreement` "contradicts itself" hotspot — found by probe, pinned by
      `a_slice_whose_declaration_names_its_own_chapter_is_reopened_in_that_chapter` (mutation-checked).
    - **Roles are counted project-wide (`SourceIndex.RolesOf`)**, so a role in the declaration, an
      earlier reopen, or a reopen moved to another file is never added twice. Opener-implied roles count
      (`View<T>()` produces T, `Query<T>()` reads T) and are never reported as removed.
    - **A slice declared outside its chapter's definition is not reopened** (another definition might
      name another model): `slice-differs-from-board`, whose `Fix` is the calls to add and where.
    - **Report-only, declared to declared** (the board's `SliceDeclaration` text vs the file's statements,
      `ImportMerge.FactsOf`): `slice-aggregate-differs`, `slice-trigger-differs`, `slice-pattern-differs`,
      `slice-role-removed-on-board`, `slice-removed-on-board`, each with a `Fix`. Only what the BOARD
      claims is compared: no board aggregate (`missing-aggregate` asked for one), a declared DCB decider,
      or a trigger added where the board states none are not differences — those are the corrections
      the import asks for, and reporting them would nag on every re-import. An inferred board aggregate
      carries an `Override` (`--aggregate`/`--starts-stream` naming the CODE's aggregate) that silences it.
    - **Removal can't be told from a hand addition**, so the messages say "leave it if you added it". A
      removed slice is checked in the definitions the import writes to and in every definition in
      `--out` with the import header (a chapter the board dropped); with `--chapter` the command passes
      the WHOLE board's slice names so a slice moved to a chapter left out isn't "removed".
  - **Roslyn locates, text inserts.** Parse the existing file (`LanguageVersion.Preview`; any syntax
    error is `merge-skipped-unparseable`), find the offset, insert re-indented text. Never re-print a
    tree: the "byte-identical except the insertion" guarantee is what the tests assert.
  - **The whole output directory is indexed (`SourceIndex`), not just the target file**, so a type,
    slice or spec class the user moved is never declared twice, and a new stub file leaves out a type
    another file declares (the layout can move a type between files when the board grows).
  - **A slice opener is a verb called on the builder itself** (`IsOpener`): `model.Command("X").Command<Y>()`
    declares slice X; matching any `Command` call took Y.
  - `--force` overwrites, as before; the spec project files are never touched; `TestSupport.cs` only
    written when missing. Idempotent: `importing_twice_changes_nothing_the_second_time`.
  - Bobcat.EventModel references `Microsoft.CodeAnalysis.CSharp` for this.

- **The curated format is retired as an authoring surface (issue #406, 2026-10-06), and what
  survives says what it is.** `CuratedModelWriter`, `FileEventModelSource` and `AddEventModelFile`
  are **gone**: there is no way to write a curated file and no way to register one as a
  Declared-rung source, which is what "YAML is not an authoring syntax" means in code. What was
  `CuratedModelFile` / `CuratedSliceShape` / `CuratedModelMapper` is renamed
  **`ImportedEventModel` / `ImportedSliceShape` / `ImportedModelMapper`**, because that is now what
  they are — the emlang importer's in-memory model, which `CSharpModelWriter` writes from and
  `ImportedModelMapper` projects to a descriptor for the `--url` push.
  - **`CuratedModelReader` is internal, not deleted, and the distinction is deliberate.** Retiring
    an authoring format means nobody can author or register one; it does not require destroying the
    parser our own fixtures are written in. `ImportedModelMapperTests` is ten fixture-driven tests
    of model-to-descriptor mapping, and rewriting them as hand-built object graphs is how coverage
    quietly changes during a retirement. It goes when those fixtures do.
  - **`EventModelFileSniffer` stays**, for one reason: it is how the import command recognises an
    old curated file and refuses it *with a sentence pointing at `EventModelDefinition`*, rather
    than reporting the "unrecognized" above. A helpful refusal is worth more than one fewer type.
  - **Two tests lost their subject rather than their claim.** A byte-for-byte YAML round-trip and
    an import-through-the-reader round-trip were both about the write/read hop; the first's
    substance was already pinned by `consumed_events_and_reads_from_are_declared_roles_of_their_own`
    (so it was deleted rather than weakened into a duplicate), and the second became
    `the_import_maps_clean_to_a_descriptor`, dropping only the hop. The C# path that replaced the
    YAML one is pinned by `CSharpModelWriterTests`, which compiles and runs its output.
  - **`*.spec-ownership.yaml` is retired with it**, and with it **BOBCAT025/BOBCAT026**. That
    removed a capability: declaring *forward*, before any code exists, how a slice will be
    specified. **The intent is that this moves onto attributes, and no such attribute exists yet.**
    `[BobcatSlice]` — binding a test that already exists to a slice — is untouched, and
    `SpecIdentityAudit` (#338, in WolverineFx.Bobcat since #744) is the only gate of its kind.
  - **Field-less is the honest output**, not a shortcut: an emlang export carries no field
    information at all (the board's props are intentionally omitted), so a name is the whole truth
    it can tell, and inventing an `Id` is a guess every consumer then has to un-guess.
  - **`--strong-typed-ids` gives each aggregate an identity type (bobcat#512/#433), and the decision
    lives in one place — `StrongTypedIds`.** Off by default: an undeclared identity is a `Guid`,
    which is what every store defaults a stream id to, and typing one is a modelling choice no board
    expresses. The name is `{Aggregate}Id` because **Marten treats a type as a strong-typed identity
    only when its name ends in `Id`** (case sensitive), so the convention IS the requirement; it
    wraps what `StreamIdentityType` says the stream's id would otherwise be. The struct is written by
    Bobcat rather than left to `wolverine scaffold`, because an import whose stubs name a type
    nothing declares does not build — and it carries `ToString`, `IParsable<T>` and a nested JSON
    converter, each needed by something an imported model routinely does (an HTTP slice posts the
    command and reads the id back).
    - **One identity per STREAM, not per type.** An aggregate's `Id`, the `Id` of a command that
      decides against it, each `{Aggregate}Id` of a command against several, the identity member of
      its events, and the `Id` of a read model projected from it are one identity
      (`IdentityOwnerOf`). The read-model arm is correctness: a single-stream projection's document
      id must BE the stream's identity type.
    - **A member is an identity by name OR by the model's mark.** `{Aggregate}Id` is the name rule;
      a stream keyed by an email has an identity member called `Email`, which only `idAttribute`
      says. Retyping happens only where the field already holds what the id type wraps, so a
      declared `string` is never silently re-read as a `Guid`.
    - **The spec writer gets the SAME instance**, and that is the whole reason `Write` takes a
      `StrongTypedIds` rather than a bool: a `Guid` local handed to an `OrderId` member is an import
      whose two halves do not compile together, which is worse than no option at all. A typed
      identity renders as `new OrderId(Guid.CreateVersion7())` and has **no table-cell form** —
      `CellValues` builds primitives and cannot construct one — so `Cell` answers null and the value
      is written as code.
    - **An aggregate whose `{Aggregate}Id` the board already uses keeps a primitive id**, with a
      `strong-typed-id-name-taken` callout. Two types of one name in one namespace is output that
      does not build, which is the one thing a scaffolder must never produce.
  - **Everything goes through `ISourceWriter`**, the rule the issue states and the reason the bump
    had to come first (jasperfx#956's whitespace fixes). Backticks are the writer's stand-in for
    double quotes, so a literal backtick in a board label is swapped for a single quote rather than
    silently becoming one.
  - **Two mistakes the first cut made, both now pinned.** The definition class took the model name,
    which is also the namespace's, so `namespace K9Crush { class K9Crush }` compiled and then
    resolved every sibling stub reference against the CLASS first — `K9Crush.SwipeOnDog` stopped
    meaning what it says. And a generic verb (`model.Command<SwipeOnDog>()`) names the slice after
    the type, so using one where the board called the slice something else would have **split one
    slice into two silently**, the slice name being the merge key; where they differ the slice now
    opens by name and states its pattern and role separately.
  - **A role whose type has no stub is declared by NAME** — `.HandledBy("SwipeEndpoint")`. A
    handler is never stubbed (an empty handler is what Wolverine's `scaffold` command exists to
    write), and a string becomes a `TypeDescriptor(name, name, "")` whose empty assembly makes the
    merge compare by `Name` (jasperfx#798), so the two spellings mean the same thing — while
    reaching for a generic over a type no stub declares simply would not compile.
  - **`CSharpModelWriterTests` compiles its own output with Roslyn, loads it, runs `Configure` and
    compares the descriptor to `ImportedModelMapper.ToDescriptor` of the same model.** Asserting on
    the emitted text would pass for a file with a missing brace, a shadowed namespace or a generic
    naming a type no stub declares — all three of which happened. The round-trip is a real guard,
    checked by dropping `.Emits` and watching it go red; `Bobcat.EventModel.Tests` therefore
    references `Microsoft.CodeAnalysis.CSharp`.

It is a plain JasperFx command host; the one server it hosts is `bobcat ui` (below), and only while
that command runs. **Its assembly is `Bobcat.Cli`, not `bobcat`** — see the resident-runner section
for why that is load-bearing rather than cosmetic; the command a person types is still `bobcat`.
`watch-event-model` and `generate-ui-contracts` came back from Stoat with the console (#524).

### The resident runner (`src/Bobcat/Residency/`, issue #390)

A suite kept available to a monitor, running specifications **when the monitor asks** — the Bobcat
half of Stoat's interactive execution. It ships as `bobcat resident <host>`; `ResidentMode.Run` is
the loop, and `BOBCAT_RESIDENT` / `--resident` are the request's spellings (cleared for a child,
below).

- **The runner is a client, and that is the whole security model.** It connects *out* to the same
  5525 origin every publisher already probes and *asks* for work; a monitor can only answer a
  runner that asked. Nothing listens on a port, so there is nothing to secure — and a command can
  only name specifications the runner already told the monitor it has, so the worst a hostile
  monitor can do is ask for a test run.
- **The publisher's invariant applies unchanged**: a monitor that is absent, slow or hostile never
  matters. No monitor means an idle process that keeps asking on a **capped** backoff (it may come
  up later, and nothing will relaunch the runner just because it did). A dropped stream reconnects
  with `Last-Event-ID`. Anything unparseable — a proxy's HTML error page, a command type from a
  newer console — is *ignored*, which is why `CloudEvent.FromJson` and `DataAs<T>` both return null
  rather than throwing. `ResidentRunner.Run` returns and never throws: a resident runner that died
  of a network blip would be worse than one quietly waiting, because its parent only relaunches it
  on a source change.
- **One command at a time, rejected rather than queued.** The monitor owns the queue; a runner that
  silently queued would leave a person waiting on a run whose turn they cannot see. **`run_finished`
  does not mean the runner is free** — the bracket closes inside the run, a hair before the
  in-flight slot clears, so a command sent the instant it arrives can still be refused as busy, and
  a client that wants a second run sends again. Invisible on a fast machine, which is why it is
  written down: the end-to-end test failed on CI three tags in a row over it. The three
  refusals are a foreign spec identity (**named**, from `SpecSelection.NotIn`), busy, and a mode
  this runner did not register — the last **never silently downgraded**, because someone who asked
  for warm and got cold would read the resulting wall clock as warm mode not working. A fourth:
  a command naming *no* specification is refused rather than read as "run everything", since the
  whole suite is what an ordinary run already does. Every refusal carries a reason, because a
  command that is simply never answered is indistinguishable from a runner that died.
  - **And a machine-readable `refusal` beside the reason (issue #400)** — `RunnerRefusal.Busy` /
    `UnknownSpec` / `UnsupportedMode` / `Empty` on `RunnerAcknowledgement`, trailing and optional
    so an older console ignores it. The split it encodes is that **busy means *not now*** and the
    other three mean *not this*: busy is the ordinary answer to a command sent the instant
    `run_finished` arrives, and the right response is to send again. With only the prose to go on,
    Stoat was matching the sentence "already running a command" — so a rewording here would have
    quietly turned every busy into a hard rejection a person reads as a failed button.
- **Cold by default** — every command launches a fresh child, so a command always runs the current
  code and a second cannot see the first's state because none of it survived. **Each command is its
  own run on the wire** — `run_started` … `run_finished`, its own `RunId`, carrying its command
  (#392).
- **`IResidentSuite` is the seam**, the same shape of decision as the supervisor's
  `IWorkerClient`: the protocol knows identities and modes, the suite knows how to run anything.
  `OutOfProcessResidentSuite` (cold) and `WarmProjectedResidentSuite` (cold and warm) implement it.
  A source change restarts the runner, so a suite's identity list describes exactly the code the
  process was built from.
- **`OutOfProcessResidentSuite` is the cold implementation (issue #399).** It holds the suite's
  `SpecManifest` and runs a command by launching **the suite's own test host** with
  `SpecFilterArguments.For`'s translation of the selection. A projected suite's entry point belongs
  to xUnit or TUnit, which will never learn what `--resident` means — so a resident runner is not
  the suite. Everything it does is a function of the manifest. **This class is cold-only and stays
  so** — it is in core, and warmth needs the supervisor's MTP client; `WarmProjectedResidentSuite`
  is what offers both (below).
  - **It ships as `bobcat resident <host>`, a command on the free tool**, because the console at
    the other end references nothing in this repository and so cannot be handed a class to host.
    That forced the tool's assembly to be renamed **`Bobcat.Cli`**: it now references core, and
    `bobcat.dll` beside `Bobcat.dll` is **one file** on a case-insensitive filesystem — on macOS
    the tool's own assembly overwrote core's in the output directory and every call into it died
    with `Could not load type … from assembly 'bobcat'`, while the same build was fine on Linux and
    would have shipped green from CI. `ToolCommandName` is still `bobcat`, so nothing a person
    types changed.
  - **A wiring mistake is refused at launch, not at the first button press**: a host that is not
    built, a suite that lists no specifications (usually a spec project with no runner adapter
    referenced), or a framework whose filter spelling Bobcat will not guess at (NUnit, MSTest). A runner
    that registered and then refused every command would look broken rather than unsupported.
  - **What the child does *not* inherit is the substance of it** (`EnvironmentFor`). Four variables
    a parent routinely sets break a commanded run silently: `BOBCAT_RUN_ID` collapses every command
    into one ever-growing run card, `BOBCAT_RUN_OWNER` stops the child publishing a run bracket at
    all, `BOBCAT_LIST_SPECS` turns a run into a listing, and `BOBCAT_RESIDENT` would have the child
    register itself instead of running (no projected host reads it today, which is exactly why it
    is cleared rather than relied upon). `BOBCAT_MONITOR=1` is *forced on* — a resident runner
    exists to serve a console, and `BOBCAT_MONITOR=0` is the kind of thing a CI job sets for a whole
    box — and `MonitorUrl` travels down as `BOBCAT_MONITOR_URL`, because a runner pointed at a
    second console with `--url` would otherwise take that console's commands and publish the runs
    to the default one. It travels **whole**, list and all (bobcat#530), while the runner registers
    with the first entry only.
- **Warm is `WarmProjectedResidentSuite`, in `Bobcat.Supervisor` (issue #402 item 4).** It wraps
  `OutOfProcessResidentSuite` — **cold is delegated unchanged**, so a cold command is byte-for-byte
  what it always was — and holds an `MtpWorkerClient` open for warm, so a warm command is one
  `testing/runTests` request into the live process. `bobcat resident` registers `cold` and `warm`; a
  console chooses per command, so nothing changes for anyone who never asks for warm.
  - **`BOBCAT_RUN_COMMAND_FILE` is the per-request command channel, and a new public variable.**
    A warm child's environment is **fixed at launch**, so `BOBCAT_RUN_COMMAND` cannot serve: every
    command after the first would carry the first one's id — precisely the mis-attribution #401
    was opened to fix. Nor is there a protocol slot: measured on MTP 1.9.1, the `runId` a client
    sends on `testing/runTests` is the client's own and is **not** the `SessionUid` the session
    handler receives (unrelated GUIDs). So the parent writes the id to a path fixed at launch
    **before** sending each request — **the exact inverse of `BOBCAT_LIST_SPECS`**, where the child
    writes what the parent reads. No race, because a resident runner runs one command at a time and
    refuses rather than queues. It **wins over `BOBCAT_RUN_COMMAND`** as the narrower claim, every
    read failure falls back to the variable and then to null (attribution must never fail a run),
    and `EnvironmentFor` **clears** it for a cold child alongside the four it already clears — a
    parent's stale file must never be read as this command.
  - **Layering: the warm suite moved UP, the client did not move DOWN.** `MtpWorkerClient` stays in
    `Bobcat.Supervisor`, because **core is what every spec project references** and an MTP JSON-RPC
    client there would tax all of them for a capability only the resident tool uses. `IWorkerClient`
    is documented as *the* seam for exactly this and #402 anticipated "a second caller of
    `IWorkerClient` rather than a new transport", so the second caller sits on the supervisor's side
    of it. `Bobcat.Console` → `Bobcat.Supervisor` is honest — driving a test host as a process is
    that tool's whole job here. (Watch the `Bobcat.Cli` assembly name while in that csproj:
    `Bobcat.Supervisor.dll` beside `Bobcat.Cli.dll` is fine, where `bobcat.dll` beside `Bobcat.dll`
    was one file on a case-insensitive filesystem.)
  - **The identity → uid join is one lookup**, per #394: the manifest's `QualifiedTestMethod`
    against the discovery display name, with `Ns.Outer+Inner` → dotted tried as a *fallback* after
    the exact match #394 verified. An identity whose uid cannot be named is **refused**, not dropped
    from the subset — a request for three specs that silently ran two looks exactly like a pass.
    **TUnit joins on the location instead (bobcat#470):** its display name is the bare method name,
    while `location.type` / `location.method` are the manifest's `TestClass` (`Outer+Inner`) and
    `TestMethod` exactly, and a data-driven method maps one identity to several uids. That is why a
    warm TUnit command can run any set of identities while a cold one is limited to what one
    tree-node path can name. `Bobcat.Supervisor.Tests/TUnitResidentSuiteTests` drives both against
    the real `Bobcat.TUnit.Tests` host.
  - **What warmth buys is the CLR, not the boot**, and the Q2 caveat stands word for word: xUnit's
    assembly and collection fixtures are created and disposed *within* a run request, so a second
    request pays for them again. A suite whose cost is a collection fixture standing up a database
    will see far less than #394's 72ms → 4ms.
  - **Withdrawal**: a faulted warm session drops `warm` from the registered modes, re-registers so
    the monitor stops offering a button that would now be refused, and refuses a warm command *with
    that reason*. **Cold is unaffected** — it starts over from exactly the thing that poisoned the
    warm host — and a cold command **closes the warm session first**, since a booted host holds the
    port, the database and the queues.
  - `Bobcat.Supervisor.Tests/ProjectedResidentRunnerTests` holds the warm tests **beside the cold
    ones**: two modes from one class, so neither gains a rule the other never hears about. One of
    them re-learned a documented rule the hard way — **`run_finished` does not mean the runner is
    free**, the bracket closing a hair before the in-flight slot clears — so a second command sent
    the instant it arrived was refused `busy`. The test waits for `ResidentRunner.Busy` to clear,
    which is what a real client's "send again" amounts to.
- **`restart` means "exit so I can be relaunched"**, and it **cuts an in-flight run short** rather
  than waiting: a wedged run is the main reason someone restarts a runner, so a restart that waited
  would be useless in exactly the case it exists for. **A restart exits 75 (`EX_TEMPFAIL`), an
  orderly stop exits 0 (issue #397).** Neither says anything about any test — the verdicts went out
  on the ingest stream as they happened, so a parent deciding whether to relaunch never has to tell
  a red suite from a crashed runner. But it does have to tell "relaunch me" from "I'm done", and
  0 could not: **0 is also what a non-resident host returns after running its whole suite**, so a
  parent relaunching on 0 would run such a suite in a loop forever. Stoat had to ask the monitor
  whether the launch had registered to work it out. `ResidentMode.RestartExitCode`, and the
  `bobcat resident` command carries it out through its input rather than JasperFx's true/false,
  which cannot say a third thing.
- **`BOBCAT_RUNNER_ID`, when set, *is* the runner's id (issue #397)** — `ResidentRunnerOptions`
  defaults to it and only then to a fresh GUID. A runner lives under a watch and is relaunched on
  every source change, so a minted id per start meant a new runner per rebuild: a command a person
  pressed while the runner was rebuilding waited on an id that never came back, the monitor's
  picker filled with dead runners, and a parent's status reports named an id the runner never
  registered under. Stoat worked around all three by superseding a disconnected runner from the
  same checkout and polling `GET /api/runners` for the minted id; handing the id over is the exact
  version of the same thing. Re-registering is idempotent, which is what makes a stable id safe.
- **Signals go through `PosixSignalRegistration`, not `ProcessExit`.** A `ProcessExit` handler runs
  after `ResidentMode.Run` has returned and disposed the `CancellationTokenSource` it would cancel,
  so the `ObjectDisposedException` aborts the process: **exit 134 from a runner that had done
  everything right**. SIGINT/SIGTERM/SIGQUIT are each an orderly stop.
- **The wire is CloudEvents, structured mode, hand-written** (`CloudEvent`, `RunnerWire`). No
  package: six scalar attributes and a payload, and the *wire shape* is the contract — the same
  standing as `Bobcat.Monitoring.MonitorEvents`, pinned by `RunnerWireContractTests` rather than by
  an assembly reference. **Ids are GUIDs** because the receiving side's mapper replaces an id it
  cannot read as one with a fresh one, which would silently break a correlation; correlation
  therefore lives in `data` (`commandId`) and never in the event id alone. Payloads are camelCase,
  because the reader is a .NET web host whose default is camelCase.
- **`ServerSentEvents` is hand-rolled**, and the reason is the TFM matrix:
  `System.Net.ServerSentEvents` is in the box on **net10.0 only** and Bobcat also targets net9.0, so
  the choice was a package on one target and a framework type on the other, or one implementation of
  the four fields used. It honours `id` (persisting across events that omit it), `event`, repeated
  `data` joined with newlines, comment lines, one optional space after each colon, and a trailing
  event with no blank line after it. **`retry` is read and dropped** — the reconnect delay is the
  runner's own backoff, so a monitor cannot make a runner reconnect in a tight loop.
- **A commanded run leaves `Session` null, and that is one rule rather than a flag (issue #401).**
  A resident runner started from an agent's terminal inherits `CLAUDE_CODE_SESSION_ID` and holds it
  for its whole life, so every run it made for a monitor command was stamped with the session that
  launched the *runner* — and Stoat's agent page said "started by this session" about runs a person
  had pressed in the UI. The fix is on `MonitorRunInfo`: `Session`'s getter returns null whenever
  `Command` is set, so a `with { Command = … }` anywhere honours it and an out-of-process
  child gets it for free from the variable. `Command` (#392) is the true answer to who asked;
  **`Tag` is unaffected**, because it answers a different question — what work the run speaks for —
  and a commanded re-run of a slice's specs is still that slice's node's.

`ResidentRunnerTests` drives the whole loop against `FakeMonitorHost` over **real HTTP**, because
the invariant under test is about an absent, slow or hostile monitor and a seam in front of the
transport would test the seam instead. `Bobcat.Supervisor.Tests/ProjectedResidentRunnerTests` goes
further: the real `Bobcat.Xunit.Samples` xUnit host, listed and then filtered to one specification
by identity, with the `bobcat resident` executable itself driven as a process so that a class
nothing ships is not mistaken for a feature. **Everything in this repository is proved against a
stand-in console** (`FakeMonitorHost` — real HTTP, not a real consumer), which is the honest limit
of what can be pinned here: Stoat references nothing in this repository and this repository
references nothing in Stoat, so neither side's CI can boot the other. The live half was done once,
on Stoat's side (stoat@0ecd122, 2026-10-04): `Stoat.ProjectedSpecs` through `bobcat resident` ran a
feature's 7 scenarios exactly. Two things only a live run could surface came out of it, both fixed:
a runner id that was per checkout rather than per project (Stoat's side), and `run_finished` not
meaning the runner is free.

## Bobcat is MIT; AI agent coordination lives in Stoat

Split 2026-08-09; the console followed Stoat on 2026-09-18, and its visual half came back on
2026-10-13 as `bobcat ui` (#524). **Bobcat is the MIT integration testing framework** — source
generator, specification recorder, runner adapters, supervisor, the publisher side of the monitor
wire, **and the visualization of what a run and a model say**: the run board, the Event Model
canvas, and the CLI queries over them. **[Stoat](https://github.com/JasperFx/stoat) is the BSL AI
agent coordination tool** — cross-repo plan DAGs, GitHub/NuGet observation, agent claims, agentic
memory, and the MCP surface. Stoat may also receive runs (on its own port, through the
`BOBCAT_MONITOR_URL` list) to show a gate's live progress; that is its own receiver, not a
dependency on `bobcat ui`.

Two rules this creates, both load-bearing:

- **Nothing here may reference Stoat, and Stoat references nothing here.** Not one-way — *zero*.
  The two sides meet only on the wire: a run POSTs `/api/ingest` and PUTs `/api/event-model` at
  whatever `BOBCAT_MONITOR_URL` names — every origin it names, since bobcat#530, which is how
  Stoat and `bobcat ui` watch one run without either knowing about the other — on the same terms
  Stoat observes GitHub and nuget.org. An
  MIT repo cannot depend on a BSL one, and keeping the dependency at zero makes that impossible to
  get wrong by accident.
- **No license gating in this repo.** Bobcat is free, entirely, `bobcat ui` included. The line is
  by subject, not by whether something remembers across a process: **showing and querying runs and
  Event Models belongs here; coordinating agents — plans, claims, memory, MCP tools — belongs in
  Stoat.** If a feature seems to want a licence gate, it is a coordination feature and goes there.

What a run publishes is therefore a **public wire contract**, not an internal detail: the events in
`src/Bobcat/Monitoring/MonitorEvents.cs` are read by `Bobcat.Ui` and by Stoat, and Stoat has no
assembly reference to warn it, so changing one of their shapes can break an external consumer
silently. `BOBCAT_RUN_TAG` is
the correlation hook — an opaque string Bobcat stamps on a run and never interprets (it was
`BOBCAT_PLAN_NODE`, renamed in the split because coordination vocabulary does not belong here).
