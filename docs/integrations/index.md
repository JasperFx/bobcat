# Integrations

Bobcat's core knows about specifications, steps and runs. It knows nothing about HTTP, databases or
message brokers — those arrive as separate packages, each one adding a **resource** (something with
a lifecycle the suite starts, resets and disposes) and a set of `IStepContext` extension methods so
your steps can reach it without holding a reference.

::: warning Three of these were removed on 2026-09-21
`Bobcat.Alba`, `Bobcat.Marten` and `Bobcat.Wolverine` are gone, along with the Wolverine half of
`Bobcat.CritterStack`. The support is being rebuilt from what real applications turn out to need.
The three pages below are placeholders recording what each carried and where the evidence is
being gathered.
:::

| Package | Status |
|---|---|
| [Bobcat.Alba](alba.md) | **Removed.** Use Alba directly; `AlbaContentRoot` survived into core |
| [Bobcat.Marten](marten.md) | **Removed.** Anything rebuilt must bind to JasperFx.Events, not Marten |
| [Bobcat.Wolverine](wolverine.md) | **Removed.** The tracked-session helpers are expected to land in Wolverine itself |

`Bobcat.CritterStack` still ships its store-agnostic half — `EventStores`, `DocumentStores`,
`EventStoreAuthoring`, `RecordBuilding` — on the JasperFx.Events abstractions, so it serves Marten,
Polecat and Fisher alike. Its Gherkin grammar and `CritterStackFixture` went with `Bobcat.Wolverine`.
`Bobcat.EntityFrameworkCore` covers EF Core and is unaffected.

The runner adapters — `Bobcat.Xunit` and `Bobcat.TUnit` — are a different kind of package and live
under Guides: [Bobcat with xUnit.net](../xunit.md) and [Bobcat with TUnit](../tunit.md).

## The shape every integration shares

```csharp
[BobcatConfiguration]
public static void Configure(BobcatRunner runner)
{
    runner.Resources.Add(new WebApp());
}
```

A resource is started once for the suite, reset between scenarios if you give it a reset hook, and
stopped at the end. Give a resource an optional `name`, and a lookup an optional `resourceName` —
that pair is how a suite drives more than one host or more than one store at once.

`WebApp` there is a resource the **sample** writes, not one Bobcat ships; every sample under
`samples/` has one, and they are worth reading as the current answer to "what does hosting an
application actually take". See [Resources](../resources.md) for the four members.

**Give the resource a reset hook if the thing behind it has persistent state.** A suite that passes
once per database and then reports conflicts for records it believes are new is worse than no suite,
and it is the default outcome for anything with a unique index.
