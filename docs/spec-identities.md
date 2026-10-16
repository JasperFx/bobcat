# Spec Identities

A Bobcat scenario's identity is `{Feature}/{Scenario}`, and it is deliberately the same string
everywhere -- the generated `SpecificationDescriptor` on the Event Model, the `scenario_finished`
event on the wire, the declared-steps key, the spec manifest. That is what lets run evidence colour a
slice on the canvas with no mapping table.

Two things follow from having one such string, and this page covers both:

| | |
|---|---|
| **[Listing and running by identity](#listing-and-running-by-identity)** | what a suite says it specifies, and how "run exactly this one" reaches its test framework |
| **[Checking identities against the model](#checking-identities-against-the-model)** | the audit that catches a spec nobody designed, and a design nothing specifies |

## Listing and running by identity

"Run this specification" has to mean the same thing whichever test framework a suite runs on,
because a monitor only ever names **identities** and never a test framework's own filter. Three
pieces make that work.

### What a suite says it specifies

Set `BOBCAT_LIST_SPECS` to a file path and the suite writes its manifest there:

```bash
BOBCAT_LIST_SPECS=/tmp/specs.json ./MySpecs --list-tests
```

```json
{
  "lane": "projected",
  "framework": "xunit",
  "suite": "Bobcat.Xunit.Samples",
  "specs": [
    {
      "identity": "Calculator/asserting values",
      "testClass": "Bobcat.Xunit.Samples.Specs.CalculatorSpecs",
      "testMethod": "asserting_values"
    },
    {
      "identity": "Facts, in one attribute/its coordinates are its traits",
      "testClass": "Bobcat.Xunit.Samples.Specs.OneAttributeSpecs",
      "testMethod": "its_coordinates_are_its_traits",
      "slice": "Selecting by coordinates",
      "chapter": "Selection",
      "domain": "Bobcat",
      "traits": { "Category": ["Coordinates"] }
    }
  ]
}
```

An entry bound to an Event Model slice also carries `slice`, `chapter`, `domain` and `pattern`
(bobcat#573). These are the values the generator folded for the Event Model, with the pattern
derived when nothing declared one. It also carries `traits`, the traits its test declares through
its framework's attributes. Each is left out when null, and they are what
[`bobcat run --slice/--chapter/--domain/--pattern/--trait`](bobcat-tool.md#selecting-by-coordinates)
resolves through.

A **file**, not stdout, because the listing has to cross a process boundary -- a resident runner is
not the suite, and *cannot* be, since the suite's entry point belongs to its test framework. Not `--list-tests` either: that prints
display names (`"Ordering: An order is accepted"`), not identities, and deriving one from the other
works right up until a feature title contains `": "`.

Writing it **can never fail the suite** -- the same invariant the monitor probe has. The listing is
the *whole* suite, not a filtered subset: what a runner registers is everything it could be asked
for.

The manifest is written from the generated module initializer, the only code Bobcat owns in that
process and the only moment sure to run. Discovery loads the assembly, so `--list-tests` reaches it
without executing a test.

`Framework` (`xunit` / `tunit` / `nunit` / `mstest`) is on the manifest because it settles the filter
spelling. `Lane` (`projected`) is on it too.

A projected suite's manifest also carries `recoveryHints` when the suite declares any
(`[ClearsOnRetry]` and friends, issue #469), so a supervisor that never loads the assembly can still
honour them. The field is left out when there are none, and a reader that predates it ignores it.
See [parallel-ready suites](parallel-ready-suites.md#recovery-hints-ride-along-in-the-spec-manifest).

### The identity, and why it is exact

`SpecIdentity` names the string that was already being built in several places -- the declared-steps
key, `scenario_finished.Uid`, the ledger, the
timing report, the generator's descriptor, the work plan's partition key.

Comparison is **ordinal and exact**. This is a machine identity, not a search, so folding case would
let two scenarios differing only in case collide into one -- silently running the wrong spec, which
is worse than a miss, because a miss is reported.

Nothing offers to *parse* an identity: a feature title may contain a `/`, so the pieces are read off
the model and never out of the string.

### Where the two halves come from

| | Default | Stated outright |
|---|---|---|
| **Feature** | the class name, a `Specs`/`Spec`/`Specification`/`Fixture` suffix stripped, read as a sentence | `[BobcatFeature("Booking appointments")]` |
| **Scenario** | the method name, underscores read as spaces (or PascalCase split) | `[BobcatSpec(Name = "A volunteer can't re-confirm, twice")]` (bobcat#489) |

An explicit name is taken verbatim, so it can carry the punctuation a method name can't -- the
apostrophe and the dash Stoat lost from two of its identities when it converted to the projected
lane. Leave it off and the identity is exactly what it always was. See
[the explicit scenario name](marker-steps.md#explicit-name).

### Each data row is its own specification

A data-driven test -- an xUnit `[Theory]` row, a TUnit `[Arguments]`, an NUnit `[TestCase]`, an
MSTest `[DataRow]` -- runs once per row, and **each row records under its own identity**
([#569](https://github.com/JasperFx/bobcat/issues/569)): the method's identity plus the row.

| Row | Identity |
|---|---|
| `[InlineData(100, Tier.Gold)]` | `Discounts/Discount is applied (amount: 100, tier: Gold)` |
| `[BobcatExample("gold customer", 100, Tier.Gold)]` | `Discounts/Discount is applied [gold customer]` |
| `[InlineData(100, Tier.Gold, Label = "gold customer")]` | `Discounts/Discount is applied [gold customer]` |
| a `[BobcatExamples]` row `\| 100 \| Gold \|` | `Discounts/Discount is applied (amount: 100, tier: Gold)` |

```csharp
[BobcatFeature("Discounts")]
public class DiscountSpecs
{
    [BobcatTheory(typeof(ApplyDiscount), Name = "Discount is applied")]
    [InlineData(100, Tier.Gold)]
    [BobcatExample("silver customer", 50, Tier.Silver)]
    public void discount(int amount, Tier tier) { ... }
}
```

- **The row part** is the arguments, named by their parameters, or the row's label when it has one.
  Values use the invariant culture, so the string is the same on every machine: strings verbatim,
  `null`, `true`/`false`, an enum by member name, numbers round-trippable, a collection as `[a, b]`,
  line breaks escaped. A label survives a changed argument, so reach for one when the row has a name.
  Only xUnit carries a label to the test; TUnit, NUnit and MSTest rows are always named by their
  arguments.
- **`[BobcatTheory]`** is `[BobcatSpec]` for a theory: the `[Theory]`, the scenario bracket and the
  slice binding in one attribute, with the same `Name`, slice settings and `Pending`. A plain
  `[Theory]` under `[BobcatScenario]` gets row identities too.
- **The Event Model keeps one specification per method.** The rows are examples of one behaviour,
  so the slice lists `Discounts/Discount is applied`, the declared and planned steps are registered
  once under it, and every row's `scenario_started`/`scenario_finished` carries it as
  **`ParentSpecId`**. A console folds the rows into the method's verdict by that field, never by
  parsing the row identity.
- **Constant rows are listed.** `[InlineData]`, `[BobcatExample]`, `[Arguments]`, `[TestCase]` and
  `[DataRow]` rows, and each row of a [`[BobcatExamples]`](marker-steps.md#bobcatexamples) table,
  are in the spec manifest with a `parent`. A row is listed only when its identity
  is certain at compile time: the value has the parameter's own type (or an integral number for an
  integral parameter, or any value for an `object` parameter). A string a runner converts to a
  `Guid`, a `params` parameter, an array or an enum value no member names is left to run time.
- **`[MemberData]`, `[ClassData]` and their kin** produce rows only a run knows. Those rows still
  record and publish under their own identities, but they are not in the manifest ahead of the run,
  so they cannot be asked for by identity.
- **Two rows of one method that format alike, or share a label, are BOBCAT036**, pointed at the row
  attributes. Change or remove the repeated row, or label it.
- **One row can't be run on its own by a filter.** A framework filter selects a test method, so a
  request for one row would run every row. `SpecFilterArguments` refuses it, naming the method's
  identity to run instead; a row asked for beside its method is already covered. A warm resident
  command refuses it too (its uid can't be named), while the method's identity runs every row.
- **Changing to row identities moves history.** A theory that recorded under the method's identity
  before this records under one identity per row now, so its ledger history and any curated link to
  the old string start again under the rows.

### Two specs, one identity

Nothing used to stop two specs from resolving to the same identity -- same-named methods in two
classes bound to one feature is all it takes -- and nothing reported it either. When it was found in
Wolverine the symptom was an unrelated test failing after a new test happened to take an existing
one's name. Everything keyed on the identity treats the two as one spec: filtering by identity, the
resident runner, the flake/duration ledger, Stoat's per-spec history, the Event Model's spec list.

It is now caught twice.

**At build time, BOBCAT036 is an error.** The generator compares the identities it is about to emit,
through the same derivation it emits them with, so it can't disagree with the runtime about case,
separators or punctuation: `order_is_placed` and `order__is_placed` collide, `an_order` and
`An_Order` don't. It is reported at each spec, naming the other with its file and line, and the fix
is a distinct method name or an explicit `[BobcatSpec(Name = "…")]`.

**At run time, the manifest write reports what the compiler couldn't see together** -- specs in two
assemblies loaded into one process, or BOBCAT036 suppressed. The generated binding used to be
last-one-wins, so the listing quietly named whichever test bound last. Now the first binding stays,
every claimant is recorded, and when `BOBCAT_LIST_SPECS` is set:

- each duplicate is reported on standard error, one line apiece, with every test that claims it;
- the manifest carries them as `duplicates`, an additive field left out when there are none;
- running a duplicated identity by name is **refused**, the same way an unknown one is.

The manifest is **still written**. Refusing the write would cost every other spec in the suite its
listing -- a resident runner with no manifest can't run anything -- to protect one identity that is
already refused by name. The build-time error is where a duplicate is meant to stop; the manifest is
the backstop, and it keeps the same invariant it always had: writing it can never fail the suite.

### A test inherited from a shared base {#inherited-specs}

A suite that writes its tests once on a shared base and runs them per subclass -- Wolverine's
`TransportCompliance<T>`, subclassed bodyless by every transport -- gets one specification per
**subclass** (bobcat#600):

```csharp
[BobcatFeature("Transport compliance")]
public abstract class TransportCompliance<T>
{
    [BobcatSpec]
    public void can_request_reply()
    {
        // Given a transport
        // Then a request gets its reply
    }
}

[BobcatFeature("TCP transport")]
public class TcpCompliance : TransportCompliance<TcpFixture>;   // TCP transport/can request reply

public class RabbitMqTransportCompliance : TransportCompliance<RabbitFixture>;
                                     // Rabbit Mq Transport Compliance/can request reply
```

- **The feature is the test class the runner runs**, never the class that declares the method. A
  subclass's own `[BobcatFeature]` names it; a base's `[BobcatFeature]` is **not inherited**, because
  every subclass taking the base's title is the collapse this exists to prevent. Without one the
  subclass's name is read as a sentence, and a generic arity (`` TransportCompliance`1 ``) never reaches
  a title.
- **The steps are the base's.** Marker comments are only ever read where the body is written, so a
  subclass's recording, `bobcat preview` and the run's rendering all show the steps the base declares.
- **The generator binds every subclass** in the compilation that runs it, reading the base from
  metadata when it lives in another assembly, so `--list-tests`, `BOBCAT_LIST_SPECS`, running by
  identity, the resident runner and BOBCAT036 all see one identity per subclass test. An **abstract**
  class binds no identity of its own: it never runs as itself.
- **What is not covered.** A base's constant `[InlineData]` rows are listed only when the base is in
  the same compilation; from another assembly they are left to the run, like `[MemberData]` rows. And
  the Event Model is not fed per subclass: a spec's slice binding and roles are read off its body, so
  an inherited spec reaches the model under the base's identity, if the base's compilation emits one.
- **Two subclasses with one title collide.** Wolverine names a class `BufferedSendingAndReceivingCompliance`
  in seven transport projects; in one compilation that is BOBCAT036, across suites it is two runs
  under one identity on `bobcat ui` or Stoat. Give each subclass its own `[BobcatFeature("…")]`.
- **Skipping one subclass's copy:** override the method with `[BobcatSpec(Skip = "…")]`, not
  `[Fact(Skip = "…")]`. xUnit inherits the base's `[BobcatSpec]`, so a `[Fact]` on the override gives it
  two Fact-derived attributes and xUnit fails it at run time ("has multiple [Fact]-derived
  attributes"). **BOBCAT040** (error) refuses that at build time.

### Asking for a subset

`SpecSelection` is the set a run was asked for, and **empty means everything**, so a run path takes
one unconditionally.

- `NarrowsAnything` tells "nothing in particular" from "asked, and it covers the suite". That
  distinction is forced on us by the test platform, which **ignores a subset parameter it does not
  understand and runs the whole suite**.
- `NotIn(known)` is how a runner rejects a foreign identity **by name, before running**, rather than
  running a narrowed suite that matched nothing and exiting 0.

### The translation, and the one thing it refuses

`SpecFilterArguments.For(manifest, selection)` turns identities into the arguments that suite's host
takes:

| Framework | Argument |
|---|---|
| xUnit v3 | `--filter-method Ns.Class.method` -- the platform uid is the framework's own (assembly + class + method + arguments), so the manifest's recorded binding is what makes the translation possible |
| TUnit | `--treenode-filter "/*/Ns/Class/method"` -- the namespace is one segment with its dots kept, a nested class is `Outer+Inner` |
| NUnit, MSTest | refused, see below |

It **refuses rather than guesses**: an unknown framework, an unbound entry, or an identity the
manifest lacks all throw.

**TUnit takes one tree-node path** ([#470](https://github.com/JasperFx/bobcat/issues/470)). The
platform accepts `--treenode-filter` once and only ORs alternatives *within* a segment, so several
identities fit in one filter only when they are several methods of one class, or one method name
across several classes (more generally, an exact cross product of namespaces, classes and methods).
Any other selection is refused rather than widened, since the extra combinations could be real tests
nobody asked for. A warm resident command runs by test id instead, so it has no such limit. A test
class in the global namespace is refused too.

**NUnit and MSTest are refused for now.** Their spelling *can* be verified here -- it just hasn't
been yet.

### The binding is recorded, not re-derived

The generated module initializer records each identity against the class and method that produced
it. The identity is a one-way function of the names -- underscores read as spaces, a `Specs` suffix
stripped -- and an explicit `[BobcatFeature("…")]` title shares nothing with its class name, so there
is no way back.

It is also emitted on a **wider rule than declared steps**, and the difference is load-bearing. A
spec declares its steps by marker comments, by step attributes, *or by vocabulary calls* -- and only
the first reaches the declared-steps registry. Keyed on that set, the first cut of this
listed **8 of `Bobcat.Xunit.Samples`' 41 specifications** while the other 33 rendered and published
verdicts perfectly well. What makes a test a specification is that a scenario is recorded around it.

It is pinned against a real host -- an identity listed and then run -- by
`ProjectedSpecIdentityEndToEndTests` in `Bobcat.Xunit.Tests/SpecIdentity`, and for TUnit by
`TUnitTreeNodeFilterTests` and `TUnitResidentSuiteTests` in `Bobcat.Supervisor.Tests`.

### Who uses this

[The resident runner](resident-runner.md). A console names an identity, the runner translates it into
the suite's framework filter, and the run arrives on the ordinary run board.

## Checking identities against the model

Nothing checked the identity against the design. A projected spec added under a scenario name the model did
not declare produced a green suite, a clean build, no diagnostic, and no warning; it survived four
commits. The retired `BOBCAT025`/`BOBCAT026` manifest checks compared **slice names**, so a test
bound to the right slice under a scenario nobody designed passed both (issue #338) -- which is why
this gate was needed even while they existed, and why retiring them in #406 took nothing away from
it.

`SpecIdentityAudit` is the join. Both facts already exist:

| artifact | says |
|---|---|
| the generated `BobcatEventModelSource` | every `{Feature}/{Scenario}` the compiled tests declare |
| the curated model | every `{Feature}/{Scenario}` the design declares |

### The gate

It is not a build diagnostic -- the generator holds the compiled identities but not the curated
model, which may not even live in the spec project. The spec assembly's **own test run** is where
both are already loaded, so the whole thing is one assertion:

```csharp
[Fact]
public void the_model_and_the_specs_declare_the_same_scenarios()
{
    var design = CuratedModelMapper.ToDescriptor(
        CuratedModelReader.Read(File.ReadAllText("../../../../model/critter-crush.yml")).File!);

    var audit = SpecIdentityAudit.Compare(design, typeof(BookingAppointments).Assembly);

    audit.Drifted.ShouldBeFalse(audit.Report());
}
```

`CuratedModelMapper` and `CuratedModelReader` come from `Bobcat.EventModel`; the audit itself is in
core `Bobcat`, and takes two `EventModelDescriptor`s -- so any design-time source works, not just a
curated file.

### What it reports

Three findings, and they are not the same thing:

- **Orphans** -- a test identity the model does not declare. The canvas shows a specification for
  something nobody designed.
- **Holes** -- a model scenario no test covers. The design declares behaviour nothing verifies.
- **Misbound** -- an identity both sides declare, on *different slices*. The spec points at the
  wrong behaviour; both slice names are real, which is why no slice-name check can see it.

Two things are reported and are deliberately **not** drift:

- **Pending** -- the identity joins, and the specification is a pending one (`[BobcatSpec(…, Pending =
  true)]`). That is already a pending-specification hotspot on the canvas; reporting it as a hole
  would read as a missing test when the test is right there, waiting.
- **Excused** -- identities you named, each with a reason. A chapter-wide invariant test that binds
  no slice, or a model scenario specified somewhere Bobcat cannot see. It carries a reason because
  "deliberately not a spec" is a claim someone should have to make out loud:

```csharp
var audit = SpecIdentityAudit.Compare(design, new Dictionary<string, string>
{
    ["BookingAppointments/Counters never go negative"] = "chapter-wide invariant, bound to no slice"
}, typeof(BookingAppointments).Assembly);
```

`Report()` is one line per finding, and a single summary line when there is nothing to say:

```
Spec identities: 41 matched, 1 orphaned, 0 uncovered, 0 misbound.

Orphans — a test declares this identity and the model does not:
  BookingAppointments/An appointment that does not exist is 404  (bound to slice ConfirmAppointment)
```

### Auditing against nothing is refused

`Compare(design, assembly)` throws when no assembly you passed has a generated Event Model source.
The generator emits one only for an assembly whose specs declare slices, so the usual causes are
naming the wrong assembly or having no `[BobcatSpec]` or `[BobcatSlice]` binding anywhere -- and reporting every declared
scenario as uncovered would be a confident lie, the same failure as zero-filling an unmeasured
duration.
