# Integrations

Bobcat's core knows about specifications, steps and runs. What it knows about your application comes
from a scenario vocabulary -- a class whose members are the steps -- and those are store- and
framework-specific.

| | Package | |
|---|---|---|
| [Marten, Polecat and Fisher](marten.md) | `Bobcat` | `CritterStackScenario` / `CritterStackSpec`: the store-only vocabulary, through the JasperFx.Events abstractions. No message bus, and no `Bobcat.Marten` package to add |
| [Wolverine](wolverine.md) | `WolverineFx.Bobcat`, `WolverineFx.Bobcat.Http` | `WolverineSpec` and `WolverineHttpSpec`: acts through Wolverine's tracked session and Wolverine.HTTP, message assertions, Dynamic Consistency Boundary steps. Published from the Wolverine repository |

Everything else -- hosting the application, a database per test class, a container -- is your test
runner's job now, done the way you'd do it in any integration test: an xUnit assembly, collection or
class fixture, `IAsyncLifetime`, TUnit's hooks. Alba is a good way to host an ASP.NET Core
application in-process, and `WolverineHttpSpec` takes an `IAlbaHost` directly.

The runner adapters -- `Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit` and `Bobcat.MSTest` -- are a
different kind of package and live under Guides: [Bobcat with xUnit.net](../xunit.md),
[Bobcat with TUnit](../tunit.md), [Bobcat with NUnit](../nunit.md) and
[Bobcat with MSTest](../mstest.md).

**Reset what has persistent state.** A suite that passes once per database and then reports conflicts
for records it believes are new is worse than no suite. Mint new ids in every specification, and where
that isn't enough, call `ResetAsync()` on the spec or `host.ResetEventStoresAsync()` from your
fixture -- plus `host.DrainTransportsAsync()` for a real broker (see
[Cold hosts and brokers](wolverine.md#cold-hosts-and-brokers)).
