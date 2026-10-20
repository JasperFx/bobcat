# Integrations

Bobcat's core is framework-neutral. It knows about specifications, steps and runs; what it knows
about your application comes from the steps you write, or from a scenario vocabulary someone else
wrote -- a class whose members are the steps.

## Wolverine, Marten, Polecat and Fisher

For a Critter Stack application, that vocabulary is **WolverineFx.Bobcat** (and
**WolverineFx.Bobcat.Http** for Wolverine.HTTP), published from the Wolverine repository:
`CritterStackSpec` for the store alone, `WolverineSpec` and `WolverineHttpSpec` for acts through
Wolverine, plus the store and broker helpers a fixture uses. It is documented in the Wolverine docs:
**[Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat)**.

Which Bobcat works with which `WolverineFx.Bobcat` is on [Version Compatibility](/compatibility).
`WolverineFx.Bobcat` is built against a specific Bobcat, so move the two together.

## Test runners

The runner adapters -- `Bobcat.Xunit`, `Bobcat.TUnit`, `Bobcat.NUnit` and `Bobcat.MSTest` -- are a
different kind of package and live under Guides: [Bobcat with xUnit.net](../xunit.md),
[Bobcat with TUnit](../tunit.md), [Bobcat with NUnit](../nunit.md) and
[Bobcat with MSTest](../mstest.md).

Everything else -- hosting the application, a database per test class, a container -- is your test
runner's job, done the way you'd do it in any integration test: an xUnit assembly, collection or
class fixture, `IAsyncLifetime`, TUnit's hooks.
