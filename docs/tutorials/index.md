# Tutorials

We're following the old Martin Fowler idea of a ["Duplex Book"](https://martinfowler.com/bliki/DuplexBook.html),
where technical content is organized first as readable tutorials, narratives focused on the problems
you're trying to solve. Then comes a second series of breakout topics on the nitty-gritty of Bobcat's APIs.

We advise you to start here on your first look at Bobcat.

| If you want to | Read |
|---|---|
| Decide what to test, at which layer, and what makes a suite worth trusting | [Test Automation Strategy Recommendations](test-automation-strategy.md) |
| Write specifications in plain C#, in the test runner you already use | [Specifications with Code](specifications-with-code.md) |
| Set up a lot of data, and verify a lot of data | [Data Intensive Specifications](data-intensive-specifications.md) |
| Run specs in a build pipeline and get useful failures back | [Integrating Bobcat with CI](continuous-integration.md) |
| Run and debug specs from your IDE's test explorer | [Integrating Bobcat with Your IDE](ide-integration.md) |
| Design a system as an Event Model and build from it with the Critter Stack | [Event Modeling and Spec Driven Development](event-modeling.md) |
| Stop a large, slow integration suite from being flaky | [Reliable Integration Testing](reliable-integration-testing.md) |
| Start a suite's Postgres, RabbitMQ or SQL Server from an Aspire AppHost instead of docker-compose | [Test Infrastructure with Aspire](aspire-test-infrastructure.md) |
| Give a coding agent a fast feedback loop: failures that say what the system did, and a rerun of only what was red | [Agent Friendly Integration Tests](agent-friendly-tests.md) |

## Bobcat's History

The original goal of Bobcat was to be an improved successor to the [Storyteller](https://storyteller.github.io/) testing tool from
the 2010's which in turn was heavily influenced by [FitNesse](https://en.wikipedia.org/wiki/FitNesse).

Bobcat is obviously influenced by [Gherkin tools like Cucumber](https://cucumber.io/docs/gherkin/) as well, but the Critter Stack
community made the decision to eschew Gherkin or any other kind of [Domain Specific Language](https://martinfowler.com/books/dsl.html) approach and instead
focus on newer .NET tricks to "project" readable specifications out of C# or F# tests written with the testing tools we already use today.
