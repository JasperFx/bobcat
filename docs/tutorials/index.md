# Tutorials

The rest of these docs are organized around Bobcat's parts — the command line, the generator, the
supervisor, the wire format. That is the right shape once you know what you are looking for, and
the wrong shape when you are starting, because it asks you to already know which part solves your
problem.

These tutorials are organized around the problem instead. Each one starts from something you are
trying to accomplish, walks the whole path, and links into the reference pages for the detail. They
are meant to be read in order and typed along with.

## Start with the one that sounds like your week

| If you want to | Read |
|---|---|
| Write specifications business people can read, in Gherkin | [Behavior Driven Development with Gherkin](bdd-with-gherkin.md) |
| Write specifications without Gherkin, in plain C# | [Specifications with Code](specifications-with-code.md) |
| Set up a lot of data, and verify a lot of data | [Data Intensive Specifications](data-intensive-specifications.md) |
| Run specs in a build pipeline and get useful failures back | [Integrating Bobcat with CI](continuous-integration.md) |
| Run and debug specs from your IDE's test explorer | [Integrating Bobcat with Your IDE](ide-integration.md) |
| Design a system as an Event Model and build from it | [Event Modeling and Spec Driven Development](event-modeling.md) |
| Stop a large, slow integration suite from being flaky | [Reliable Integration Testing](reliable-integration-testing.md) |
| Make failures legible to a coding agent | [Agent Friendly Integration Tests](agent-friendly-tests.md) |

## How these differ from the rest of the docs

A tutorial teaches a path. A reference page pins a fact. When the two disagree, the reference page
is right — tutorials link to the reference rather than restating it, precisely so there is one
place each fact lives and one place to fix it.

The pages under **Guides** are how-to: a single task, assumed context, no narrative. The pages
under **Reference** are lookups. Several pages are records of a design decision or a rollout,
written when it happened — they explain *why* Bobcat is shaped the way it is, and they are the
right read when a behaviour surprises you.
