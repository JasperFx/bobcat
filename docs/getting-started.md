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

## Next

- [Sample Wiring Playbook](sample-wiring.md) — wire a sample host to `BobcatRunner`
- [Parallel-Ready Suites](parallel-ready-suites.md) — what a suite needs before the supervisor splits it
- [Version Matrix](versions.md) — the canonical, mutually-compatible dependency set
