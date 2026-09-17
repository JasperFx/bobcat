# Checking Spec Identities Against the Model

A Bobcat scenario's identity is `{Feature}/{Scenario}`, and it is deliberately the same string
everywhere — the generated `SpecificationDescriptor` on the Event Model, the `scenario_finished`
event on the wire, the retry budget's test id. That is what lets run evidence colour a slice on
the canvas with no mapping table.

Nothing checked it against the design. A projected spec added under a scenario name the model did
not declare produced a green suite, a clean build, no diagnostic, and no warning; it survived four
commits. `BOBCAT025` and `BOBCAT026` compare **slice names**, so a test bound to the right slice
under a scenario nobody designed passes both (issue #338).

`SpecIdentityAudit` is the join. Both facts already exist:

| artifact | says |
|---|---|
| the generated `BobcatEventModelSource` | every `{Feature}/{Scenario}` the compiled tests declare |
| the curated model | every `{Feature}/{Scenario}` the design declares |

## The gate

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

## What it reports

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

## Auditing against nothing is refused

`Compare(design, assembly)` throws when no assembly you passed has a generated Event Model source.
The generator emits one only for an assembly whose specs declare slices, so the usual causes are
naming the wrong assembly or having no `@slice:` tag anywhere — and reporting every declared
scenario as uncovered would be a confident lie, the same failure as zero-filling an unmeasured
duration.
