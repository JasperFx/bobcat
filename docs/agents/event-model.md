# The Event Model: slices, descriptors, roles, query slices

### Event Modeling slices and descriptors (issues #104/#106)

A projected test declares the non-derivable bits of an Event Modeling slice on `[BobcatSlice]` or
`[BobcatSpec]`: the slice (`SliceType`, or a literal `SliceName`), `Domain`, `Chapter`, `Pattern`
and `TriggeredBy`. `GeneratorSliceTags` reads the first four in the generator; `TriggeredBy` is read
on its own (#463, below). A method-level binding **replaces** the class's for that test rather than
overriding part of it — a rebinding is a whole binding, so a test that wants the class's domain too
says so. A method's `TriggeredBy` beats its class's whatever order tests fold in
(`TriggerLabelDeclaredOnScenario`, the #258 rule).

**The generator turns these into a JasperFx `EventModelSliceDescriptor` (issue #106)** —
Bobcat is the first real implementation of `IEventModelDefinitionSource` anywhere. One
`BobcatEventModelSource.g.cs` per assembly, registered with `services.AddBobcatEventModel()`.

- **`[assembly: EventModelName("…")]` names the model; the assembly name is only the fallback
  (issue #172).** Upstream, `EventModelDiscovery.Assemble` folds descriptors together **by model
  name**, and the Wolverine-derived model is named for the *service* (`opts.ServiceName`) — so a
  spec assembly called `BankAccountES.Tests` can never merge with the chains it describes unless
  it declares the service's name. The attribute renames only the descriptor; the source's
  `Subject` URI keeps the assembly name, because two spec assemblies may legitimately feed one
  model. `samples/BankAccountES` is the worked example (`Tests/AssemblyAttributes.cs`), and its
  `EventModel.cs` specs pin the merge end to end.
- **The generated source is `internal`, and that has a consequence:** the app host cannot see the
  spec assembly's slices (the reference points the other way), so composing all four design-time
  sources — chains + store + overlay + specs — inside one process only works in the spec runner's,
  where `samples/BankAccountES/Tests/EventModelSteps.cs` does it. **The store rung** (issue
  #300, jasperfx#825): since JasperFx.Events 2.69 every Critter Stack store registers a
  `ProjectionEventModelSource` from `AddMarten`/`AddFisher`/`AddPolecat` — one `View` slice per
  registered projection, named after the **document type**, carrying `ProjectionTypes`,
  `ReadModelTypes` and `ConsumedEvents` from the projection's real apply set. Slices merge by name,
  so a spec-declared View slice folds into it only if the binding names the document type exactly
  (`[BobcatSpec(typeof(Account))]`); `AccountView.cs` + `EventModel.cs` in the sample pin "one
  slice, not two", and the falsification (mis-bind it) shows as the `Specifications`/`Domain` roles
  going unclaimed on the store's slice, not as a second slice appearing. A handler that starts a
  stream through `Storage.StartStream` needs Wolverine's `[Emits(typeof(…))]` before any source
  knows what it appends — the event is built in the body, so nothing on the signature says it
  (wolverine GH-4386) — and without it the store's view has no `EventConsumed` link back to that
  slice. The host's own `event-model --url` export (and therefore `bobcat watch-event-model`)
  pushes a document *without* the spec-declared slices or their `Specifications` bindings. **The two
  halves meet on the console — `bobcat ui`, or Stoat — instead:** #268 made `/api/event-model` merge per source
  and #294 made the runner publish the spec half as it attaches, so each producer pushes what it can
  see and `GET` folds them by slice name. That is the whole reason the `[EventModelName]` above is
  load-bearing rather than cosmetic — the merge is keyed on the model name at both altitudes.
- **An overlay's human trigger label on an HTTP slice works again as of WolverineFx 6.31.0**
  (wolverine#4181/#4182, both fixed by wolverine#4185). Before it, the HTTP-derived source
  claimed `TriggerLabel` with the verb+route, so the overlay's label lost the merge and minted a
  noise `SourceDisagreement` hotspot per labelled slice — and a query endpoint returning
  `IReadOnlyList<T>` reported the raw generic CLR string as its read model. The specs in
  `samples/BankAccountES/Tests/EventModel.cs` that assert the fixed behaviour are the regression
  guard — don't delete them as redundant.
- **A slice accumulates across the whole compilation.** Several specs usually describe the same
  slice, and a slice may span several test classes; every test bound to it feeds one descriptor.
- **Only roles are stamped, never a graph.** `EventModelSliceDescriptor.Elements`/`.Edges` are
  *computed upstream* from the typed roles on every read. Building the element graph in the
  generator would be a second opinion about the same slice, which is what "computed on read"
  exists to prevent.
- **`Chapter` is the third slice setting (issue #298, canvas design decision 5).** A chapter is the
  span of the timeline a slice belongs to — the grouping every Event Modeling tool zooms into
  (eventmodelers.ai's wide arrow, Miro's frames, emlang's per-chapter documents) — and it is
  **independent of `Domain`**: a bounded context has many chapters. `EventModelEmitter` stamps it
  onto JasperFx 2.69's `Chapter` role (which merges like `Domain`: per claim, first wins on a tie, a
  genuine disagreement is a `SourceDisagreement` hotspot). `EmlangImport` keeps the board's chapter
  name — every imported slice carries the chapter it was segmented from, and a slice folded from a
  second chapter **keeps the first** with a report line saying so. `@jasperfx/event-model-vue` 0.12.0
  draws **one band per contiguous run** of same-chapter slices; **the canvas never reorders
  slices**, and whether something upstream should order chapters is an open decision (2026-09-15).
- **An arranged event is what a View slice consumes, and nothing on a Command slice (issue #297,
  canvas design decision 3).** Arranging `AccountOpened` and then asserting the Account read model
  is the best evidence there is that the projection applies `AccountOpened`, and *consumed* is a
  different claim from *emitted* — JasperFx 2.69's `ConsumedEvents` role, the `EventConsumed` link,
  the State View arrow. On a Command slice the same arrangement is the aggregate's own stream, and
  stamping it would draw an arrow from `OpenWallet` into every slice that merely starts from an
  open wallet. Mechanics: the `ArrangedEvent` role collects into `SliceModel.ArrangedEvents`, and
  `emitSlice` writes `ConsumedEvents` only when `patternOf` is `View` — so the decision is per
  *slice*, after every test has folded in (one acting test makes the whole slice Command). **Type
  lists merge by equality, not union**: a View slice whose specs between them arrange fewer events
  than the store's projection applies is a `SourceDisagreement` hotspot, and the right reading (the
  view is under-specified). The sample's two `AccountView.cs` specs arrange all four of the Account
  projection's events so the store rung and the spec rung agree; drop one and the sample goes red.
  `ReadsFrom` reaches a model through the importer, and from a spec through `SliceRole.Context`
  (`GivenContext`, #502, below).
- **The command is the act — the last `Command` call, not the first one named (#464).** Specs
  routinely arrange by issuing earlier commands (an `OpenWallet` before the `CreditWallet` the test
  is about), so first-wins mislabelled the slice. Arrange commands are not lost: they stay in the
  specification's `ResolvedTypes`, which is where "this spec touched that type" belongs and what
  #107's run evidence joins on.
- **Gated on the reference, by type probe.** Nothing is emitted unless the consuming compilation
  has `JasperFx.Events.EventModeling.EventModelSliceDescriptor` — most Bobcat suites do no event
  sourcing. A type probe rather than an assembly-name check because JasperFx.Events **2.53.0
  shipped an early, incompatible sketch** of that namespace; "the assembly is referenced" and
  "the shape emitted against exists" are different questions. Requires **2.54.0+**.
- **Spec identity is `{Feature}/{Scenario}`** — the same string `scenario_finished` carries, so
  design-time and run evidence join with no mapping table.
- **`Pattern` is derived only where the test can tell**: a slice whose act sends a command is
  `Command`; one whose act is a read (below) and none a command is `Query`; one that only asserts a
  read model is `View`. `Automation` and `Translation` need a trigger the test does not express, so
  they stay null unless `Pattern` declares them — a wrong pattern miscolours the canvas, a null one
  does not.
- **A read-only act is a Query slice (bobcat#501, JasperFx 2.85.0's `SlicePattern.Query`).** A vocabulary
  method with `[EventModelTrigger(TriggerKind.Http, HttpMethod = "GET"/"HEAD")]`, a `ReadModel` role and no
  `Command` role is a read act (`ProjectedRoles.isReadTrigger` → `Reading.ReadAct`); the LAST act — command or
  read — is the test's act. **Inferred, no marker**: the trigger attribute is what separates a read act from an
  assertion (`ThenReadModel<T>` carries the same role and no trigger), and the verb separates it from a write, so a
  `SliceRole.QueryResult` or `Query = true` would restate what is already there. `patternOf`: declared → act
  command → `ReadActs` → any command → read model. On a Query `emitSlice` writes no `CommandType` (an arranging
  command is not its act), no `ReadModelTypes`, no `AggregateTypes`, no `ConsumedEvents`, and folds read models
  (the read act's type, asserted read models) into `ReadsFrom` beside any `GivenContext` (#502), so `ReadModelRead`
  links the read model's View to the query. **A stream arranged with plain `GivenEvents` stamps nothing on a
  Query** — it is setup feeding a projection, not a read; an earlier cut put it in `ReadsFrom`, which linked every
  slice producing `Account` to the transactions query. A read act's trigger (`TriggerFromRead`) is
  replaced by any command act's and stamped only on a Query. The manifest skips `inferCommand` for a read act.
  Wolverine derives GET chains as View until wolverine#4975, so BankAccountES's two GET slices merge as View with
  a Pattern `SourceDisagreement` — pinned in `EventModel.cs`, filtered out of the "exactly one planted
  disagreement" count by `isQueryDerivedAsView`.
- **Projected specs feed the model, and the identity has three copies.** `MarkerCommentSpecs` reads
  the tests and `EventModelEmitter.Collect` folds them into one slice dictionary; a test with
  neither a binding nor roles contributes nothing. The identity is `{derived feature title}/{derived
  scenario title}`, derived by `Bobcat.Generators.MarkerSpecNaming` at compile time and
  `Bobcat.MarkerSpecNaming` (through `MarkerStepRun`) at run time.
  **`MarkerSpecNamingAgreementTests` pins the two together** — that string is looked up by
  `DeclaredSteps.For(Uid)` AND published on `scenario_finished`, so a divergence costs a projected
  suite both its rendered steps and its place on the model, silently. The rule: `[BobcatFeature]`'s
  title verbatim when present; else strip one `Specification`/`Specs`/`Spec`/`Fixture` suffix, read
  underscores as spaces (dropping empty segments), else split PascalCase. Scenario titles use the
  same reading of the method name, unless `[BobcatSpec(Name = "…")]` states it.
  `ProjectedSpecNaming.RoundTrips` in `Bobcat.EventModel` is a third copy of the scenario half —
  that assembly references neither — pinned by `ProjectedSpecNamingAgreementTests`.
- **A projected spec's Event Model ROLES come off its vocabulary (bobcat#462, #463).** Scenario
  vocabulary methods carry `[EventModelRole(SliceRole.X, nameof(param or TParam))]` (and the act
  `[EventModelTrigger(TriggerKind.Http, Route = nameof(route))]`, or `RouteName = nameof(routeName)`), both in core
  (`src/Bobcat/EventModel/EventModelRoleAttribute.cs`). `ProjectedRoles.Read` walks a test method's
  invocations through `IInvocationOperation` and reads them off the resolved symbol — **from
  metadata, never by method name**, because the vocabulary (`WolverineScenario`) lives in another
  repository and a rename there must not silently stop the stamping. `MarkerCommentSpecs.Extract`
  stores the reading on `MarkedScenario.Roles`; `EventModelEmitter.Collect` folds it into the
  `SliceModel` lists that `patternOf` / `emitSlice` read.
  - **The rules:** the act is the LAST `Command` call or read act (#464, #501), and its trigger is read only off
    that call; `ArrangedEvent` → `ArrangedEvents` → `ConsumedEvents` only on a View slice (#297);
    `Document` → resolved types only (a document is not an aggregate, event or read model, so it
    gets no element on the canvas); `Context` → `SliceModel.ReadsFrom` → `ReadsFrom`, on any pattern
    and never `AggregateTypes` (#502: `GivenContext<Client>` for a stream the act only looks up, written
    only when non-empty); an argument whose type is not knowable (`object`, interface,
    rows, `null`) stamps nothing. `Specified<T>`, `ExpectedValue<T>` and `TaggedValue` are unwrapped
    by metadata name — Bobcat's own types, so the same-repo contract.
  - **A test with roles and no `[BobcatSlice]` lands on a slice named after its feature** — unless the feature's
    unbound tests make different acts (bobcat#615): then a command act goes to the slice named for its command, and
    a read act or an act naming no command (`[EventModelAct]`, an anonymous body) goes nowhere, as BOBCAT041. A
    slice whose act names no command is never inferred a View (`SliceModel.HasUnstampedAct`).
  - `Specifications[].ResolvedTypes` (#107's join target) is filled here, and the same list
    rides `SpecificationBindingDescriptor.ResolvedTypes` from `SpecificationManifest` — gated on the
    property existing in the consumer's JasperFx. The manifest prefers the role-stamped act over its
    name-based `inferCommand`, which stays as the fallback for a vocabulary with no roles.
  - **`TriggeredBy` on `[BobcatSlice]` / `[BobcatSpec]` is the trigger label** (#463), read like
    `Pending` rather than as a tag (no second copy of `GeneratorSliceTags` to keep in step). Method
    beats class regardless of fold order (#258, via `TriggerLabelDeclaredOnScenario`); a method
    attribute that sets only `TriggeredBy` keeps the class's slice (and the manifest agrees,
    `onlyTriggeredBy`). `TriggerKind` is deliberately NOT declarable there — upstream says it is
    derived, never declared.
  - **BOBCAT034** (error): a `From`/`Route`/`RouteName` naming no parameter, reported where the vocabulary is
    compiled. `ProjectedEventModelRoleTests` pins all of it, including a vocabulary compiled into a
    separate assembly (metadata path).
  - **The act's route (bobcat#500, `TriggerRoutes`).** A constant `Route` is stamped as written. A constant
    `RouteName` (the second trigger argument, `RouteName = nameof(routeName)`) resolves to the template of
    the one endpoint attribute declaring it. An interpolated (or `+`-concatenated) `Route` takes the template
    of the **one** endpoint whose literal segments match, with a parameter at every hole and a verb that
    agrees when both are known. With no single match it is rebuilt from the interpolation (identifier → its
    name, member access → last member, else `{value}`, repeats numbered) and marked reconstructed;
    `adoptRoute` ranks read/resolved > reconstructed > unknown, order-independent. Endpoints are matched
    **by attribute shape and metadata name, never a package reference**: anything deriving from
    `Wolverine.Http.WolverineHttpMethodAttribute` (`RouteName`, class `[RoutePrefix]` applied as Wolverine's
    `RoutePrefixPolicy` does) and MVC's `Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute` /
    `Microsoft.AspNetCore.Mvc.RouteAttribute` (`Name`, controller `[Route]` + `[controller]`/`[action]`).
    The index is built once per `Compilation` (a `ConditionalWeakTable`) from source plus only those
    references that reference `Wolverine.Http` or `Microsoft.AspNetCore.Mvc.Core`. Minimal API
    `.WithName` and Wolverine's namespace/global prefixes are run-time facts and are not resolved.
    **The route name rides `PublisherOrigin.HttpRouteName`** (JasperFx 2.85.0), next to the route it resolved
    to (`adoptRoute` carries it with the route). An unresolved name is stamped on an origin with no route and
    `Label = "PUT (route name X)"`; `Label` still never carries a name beside a route, because it takes part in
    the `TriggerOrigin` merge. JasperFx merges the name on its own (a known name fills in; two differ → hotspot).
  - **BOBCAT038** (info): the act's `Route`/`RouteName` can't be turned into a route — a helper call or a
    variable, an unknown or ambiguous route name, or none passed. Reported for the act only, at the
    argument, through `MarkedProblem` ("UnresolvedRoute"). `TriggerRouteTests` pins every path, including a
    route name resolved from a referenced assembly's metadata; `samples/BankAccountES`'s `update_a_client`
    addresses its endpoint by route name and `EventModel.cs` checks the specs' own route agrees with
    Wolverine's.
- **`[BobcatSpec(…, Pending = true)]` is a pending specification (issue #404)** — a
  `HotspotDescriptor.PendingSpecification` (jasperfx#689) that `SpecIdentityAudit` reads as
  **joined, not drift**. Without it a scaffolded skeleton *threw* and a pending spec was
  indistinguishable from a failing one. `EventModelEmitter.Collect` routes the identity to
  `PendingSpecifications` instead of `Specifications` — both put it on the slice, which is what
  keeps it joined (not an orphan, and its slice not a hole) while stopping a stub from looking like
  evidence.
  - **Skipped, never swallowed.** `Pending = true` sets `FactAttribute.Skip`, so every runner and
    IDE reports it the way it reports any skip: nobody ran it. Running the body and absorbing the
    failure was the alternative and is exactly how red gets laundered into green — the same
    guardrail as the ledger's refusal to feed proposals back into a policy. An explicit `Skip`
    wins in either written order, which is two tests because property initializers run in written
    order.
  - **"No steps" is NOT read as pending.** A projected test with no marker comments says plenty, and
    **33 of this repo's own 41 projected samples declare no marker steps at all**. Inferring it
    would have turned most of a working suite into open questions.
  - **On `[BobcatSpec]` only, not on `[BobcatSlice]`.** Putting it on the slice attribute would let
    a TUnit suite write it today, but that attribute is not the test: it would mint the hotspot
    while the body still ran and still failed — the exact state #404 exists to end. A partial
    capability that looks complete is worse than an absent one.
  - **Carried on `MarkedScenario.Pending` with its own reader, not as a slice tag.** Pending is a
    property of one specification; the tag vocabulary is slice grouping, and routing it through
    `GeneratorSliceTags` would carry something no slice has an opinion about.
  - A pending spec **stays in the spec manifest and in `--list-tests`** — xUnit discovers a skipped
    test like any other.
