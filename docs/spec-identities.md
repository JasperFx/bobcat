# Spec Identities

A Bobcat scenario's identity is `{Feature}/{Scenario}`, and it is deliberately the same string
everywhere — the generated `SpecificationDescriptor` on the Event Model, the `scenario_finished`
event on the wire, the retry budget's test id, the platform's own uid. That is what lets run evidence
colour a slice on the canvas with no mapping table.

Two things follow from having one such string, and this page covers both:

| | |
|---|---|
| **[Listing and running by identity](#listing-and-running-by-identity)** | what a suite says it specifies, and how "run exactly this one" reaches either lane |
| **[Checking identities against the model](#checking-identities-against-the-model)** | the audit that catches a spec nobody designed, and a design nothing specifies |

## Listing and running by identity

"Run this specification" has to mean the same thing whichever lane a suite is written in, because a
monitor only ever names **identities** and never a test framework's own filter. Three pieces make
that work, and the asymmetry between the lanes is the whole of it.

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
    }
  ]
}
```

A **file**, not stdout, because the listing has to cross a process boundary — a resident runner is
not the suite, and for a projected suite it *cannot* be. Not `--list-tests` either: that prints
display names (`"Ordering: An order is accepted"`), not identities, and deriving one from the other
works right up until a feature title contains `": "`.

Writing it **can never fail the suite** — the same invariant the monitor probe has. The listing is
the *whole* suite, not a filtered subset: what a runner registers is everything it could be asked
for.

Each lane answers at the moment it can:

| | |
|---|---|
| **Gherkin** | at discovery — the moment the features are scanned and nothing has executed. `BobcatRunner.SpecIdentities` / `.Manifest()` / `.NarrowTo(selection)` are the in-process surface |
| **Projected** | from the generated module initializer, the only code Bobcat owns in that process and the only moment sure to run. Discovery loads the assembly, so `--list-tests` reaches it without executing a test |

`Lane` (`gherkin` / `projected`) and `Framework` (`bobcat` / `xunit` / `tunit`) are both on the
manifest, because the lane does not settle the filter spelling: a projected suite is filtered by
*its framework's*.

### The identity, and why it is exact

`SpecIdentity` finally names the string that was already being built in eight places — the platform
uid, the retry budget's test id, the declared-steps key, `scenario_finished.Uid`, the ledger, the
timing report, the generator's descriptor, the work plan's partition key.

Comparison is **ordinal and exact**. This is a machine identity, not a search, so folding case would
let two scenarios differing only in case collide into one — silently running the wrong spec, which
is worse than a miss, because a miss is reported.

Nothing offers to *parse* an identity: a feature title may contain a `/`, so the pieces are read off
the model and never out of the string.

### Asking for a subset

`SpecSelection` is the set a run was asked for, and **empty means everything**, so a run path takes
one unconditionally.

- `NarrowsAnything` tells "nothing in particular" from "asked, and it covers the suite". That
  distinction is forced on us by the test platform, which **ignores a subset parameter it does not
  understand and runs the whole suite**.
- `NotIn(known)` is how a runner rejects a foreign identity **by name, before running**, rather than
  running a narrowed suite that matched nothing and exiting 0.

In the Gherkin lane `NarrowTo` **composes with** the existing scenario filter rather than replacing
it, because an MTP host has already set the platform's uid filter and a selection must never widen
past it.

### The translation, and the one thing it refuses

`SpecFilterArguments.For(manifest, selection)` turns identities into the arguments that suite's host
takes:

| Lane | Argument |
|---|---|
| Gherkin | `--filter-uid "Ordering/An order is accepted"` — the identity **is** the platform uid, so it goes through unchanged |
| Projected (xUnit v3) | `--filter-method Ns.Class.method` — the uid there is the framework's own (assembly + class + method + arguments), so the manifest's recorded binding is what makes the translation possible |

It **refuses rather than guesses**: an unknown framework, an unbound entry, or an identity the
manifest lacks all throw.

**TUnit is deliberately unsupported.** It filters by tree-node path, and nothing here can run a TUnit
host to verify the spelling — `TUnit.Engine` needs a newer testing platform than this repository is
pinned to. An unverified filter is exactly the run that looks filtered and is not.

### The projected binding is recorded, not re-derived

The generated module initializer records each identity against the class and method that produced
it. The identity is a one-way function of the names — underscores read as spaces, a `Specs` suffix
stripped — and an explicit `[BobcatFeature("…")]` title shares nothing with its class name, so there
is no way back.

It is also emitted on a **wider rule than declared steps**, and the difference is load-bearing. A
projected spec declares its steps by marker comments, by step attributes, *or by grammar calls* —
and only the first reaches the declared-steps registry. Keyed on that set, the first cut of this
listed **8 of `Bobcat.Xunit.Samples`' 41 specifications** while the other 33 rendered and published
verdicts perfectly well. What makes a test a specification is that `[BobcatScenario]` records it.

`Bobcat.Mtp.Tests/SpecIdentityEndToEndTests` pins **both lanes from one class**, against the real
hosts — one identity per lane, listed and then run. Deliberately not split across two test projects:
that is how one lane gains a rule the other never hears about.

### Who uses this

[The resident runner](resident-runner.md). A console names an identity, the runner translates it into
its own lane's filter, and the run arrives on the ordinary run board. That is the reason an identity
can be a lane-neutral request at all — both lanes genuinely are MTP hosts.

## Checking identities against the model

Nothing checked the identity against the design. A projected spec added under a scenario name the model did
not declare produced a green suite, a clean build, no diagnostic, and no warning; it survived four
commits. The retired `BOBCAT025`/`BOBCAT026` manifest checks compared **slice names**, so a test
bound to the right slice under a scenario nobody designed passed both (issue #338) — which is why
this gate was needed even while they existed, and why retiring them in #406 took nothing away from
it.

`SpecIdentityAudit` is the join. Both facts already exist:

| artifact | says |
|---|---|
| the generated `BobcatEventModelSource` | every `{Feature}/{Scenario}` the compiled tests declare |
| the curated model | every `{Feature}/{Scenario}` the design declares |

### The gate

It is not a build diagnostic — the generator holds the compiled identities but not the curated
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
core `Bobcat`, and takes two `EventModelDescriptor`s — so any design-time source works, not just a
curated file.

### What it reports

Three findings, and they are not the same thing:

- **Orphans** — a test identity the model does not declare. The canvas shows a specification for
  something nobody designed.
- **Holes** — a model scenario no test covers. The design declares behaviour nothing verifies.
- **Misbound** — an identity both sides declare, on *different slices*. The spec points at the
  wrong behaviour; both slice names are real, which is why no slice-name check can see it.

Two things are reported and are deliberately **not** drift:

- **Pending** — the identity joins, and the test scenario has no steps. That is already a
  pending-specification hotspot on the canvas; reporting it as a hole would read as a missing test
  when the test is right there, empty.
- **Excused** — identities you named, each with a reason. A chapter-wide invariant test that binds
  no slice, or a model scenario specified in a lane Bobcat cannot see. It carries a reason because
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
naming the wrong assembly or having no `@slice:` tag anywhere — and reporting every declared
scenario as uncovered would be a confident lie, the same failure as zero-filling an unmeasured
duration.
