# Getting Started

Bobcat is a toolset for authoring, supervising, and executing integration tests in .NET.

```bash
dotnet add package Bobcat
```

## Your first feature

```gherkin
Feature: Customer registration

  Scenario: A new customer is created
    Given a customer named "Ada"
    When the customer list is requested
    Then the response contains "Ada"
```

Bind the steps to ordinary methods on a fixture:

```csharp
[Given("a customer named {string}")]
public Task Customer(string name)
    => Context.PostJsonAsync("/customers", new CreateCustomer(name));
```

Prefer C# to `.feature` files? The same engine runs [code-first specifications](code-first-specs.md) —
`[Scenario]` methods on a `Specification` class, no generator involved — and both styles render,
supervise, and report identically.

Already have a large test suite you would rather not rewrite? [Specs from tests you already
have](marker-steps.md) turns existing xUnit tests into specifications with marker comments and
decorated helpers, one class at a time.

## Next

- **[The Tutorials](tutorials/)** — the whole documentation set organized by what you are trying to accomplish, rather than by what Bobcat is made of. Start there if you are not sure which page you want
- [The Command Line](command-line.md) — `run`, `list`, `preview` (step bindings, nothing executed), and the warm-suite `interactive` mode
- [Running Specs with `dotnet test`](dotnet-test.md) — the generated MTP entry point, `[BobcatConfiguration]`, and the `--filter-feature`/`--filter-tag` options
- [Specs From Existing Tests](marker-steps.md) — marker comments and `[BobcatStep]` helpers, for a suite you would rather not rewrite
- [Sample Wiring Playbook](sample-wiring.md) — wire a sample host to `BobcatRunner`
- [Editor Integration](editor-integration.md) — step completion and go-to-definition in VS Code and Rider
- [Make Existing Integration Tests More Reliable](parallel-ready-suites.md) — the supervisor: worker-process splitting, per-lane resource isolation, retry budgets, and what a suite needs before it can be split
- [What a Run Publishes](monitor-design.md) — the monitor wire contract, `BOBCAT_MONITOR*`, and the seams that emit it
- [The `bobcat` Tool](command-line.md#the-bobcat-tool) — reading, validating and importing Event Model files
