# Integrations

Bobcat's core knows about specifications, steps and runs. It knows nothing about HTTP, databases or
message brokers — those arrive as separate packages, each one adding a **resource** (something with
a lifecycle the suite starts, resets and disposes) and a set of `IStepContext` extension methods so
your steps can reach it without holding a reference.

| Package | What it gives you |
|---|---|
| [Bobcat.Alba](alba.md) | An ASP.NET Core host, driven over HTTP through Alba |
| [Bobcat.Marten](marten.md) | A Marten document store and event store |
| [Bobcat.Wolverine](wolverine.md) | Wolverine message tracking, handler warm-up and transport draining |

Two more exist and are documented with the features that use them: `Bobcat.CritterStack` ships the
Gherkin grammar for Critter Stack applications (see [Composing Grammar Modules](../composing-grammars.md)),
and `Bobcat.EntityFrameworkCore` covers EF Core.

The runner adapters — `Bobcat.Xunit` and `Bobcat.TUnit` — are a different kind of package and live
under Guides: [Bobcat with xUnit.net](../xunit.md) and [Bobcat with TUnit](../tunit.md).

## The shape every integration shares

```csharp
[BobcatConfiguration]
public static void Configure(BobcatRunner runner)
{
    runner.Suite.AddResource(new AlbaResource<Program>());
}
```

A resource is started once for the suite, reset between scenarios if you give it a reset hook, and
disposed at the end. Every integration's resource takes an optional `name`, and every step-context
extension takes an optional `resourceName` — that pair is how a suite drives more than one host or
more than one store at once.

**Give the resource a reset hook if the thing behind it has persistent state.** A suite that passes
once per database and then reports conflicts for records it believes are new is worse than no suite,
and it is the default outcome for anything with a unique index.
