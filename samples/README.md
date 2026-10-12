# CritterStackSamples

Sample projects using the "Critter Stack" tools ([Marten](https://martendb.io) and [Wolverine](https://wolverinefx.net)) and related [JasperFx](https://github.com/jasperfx) projects.

Most of these samples require PostgreSQL:

```bash
docker compose up -d
```

That publishes Postgres on **5433** — the port every sample's default connection string already
names — and creates one database per sample. To use a PostgreSQL you already run, point the
sample's `appsettings.json` at it instead.

## Running a sample's specs

Every sample with specs carries a `Tests/` subdirectory of Bobcat's projected (code-first) specs:
ordinary xUnit v3 tests that render as Given/When/Then specifications. There are no `.feature` files.

```bash
cd PaymentsMonolith
dotnet test Tests/Tests.csproj                     # run them
BOBCAT_SPEC_CONSOLE=1 dotnet run --project Tests   # ...and print every spec as it rendered
```

Each `Tests/` project boots the host once through Alba (`AlbaHost.For<Program>()`, an xUnit assembly
fixture), and a base spec class in the same folder holds the HTTP calls and empties the store before
every spec. The event-sourced samples -- BookingMonolith, MeetingGroupMonolith, ProjectManagement and
BankAccountES -- derive that base from `CritterStackSpec`, so a spec can also assert the events a
request appended and the aggregate they built.

Two settings in each `Tests.csproj` are what a package consumer would inherit from Bobcat's
`buildTransitive` props and a `ProjectReference` does not: `InterceptorsNamespaces` (without it a
`[Given]` / `[When]` / `[Then]` helper records nothing and `SpecAssert` reports to no step, so every spec
passes vacuously) and the `Bobcat.Xunit` `TestingPlatformBuilderHook`.

## Samples

| Sample | Original Project | Description | Patterns |
|--------|-----------------|-------------|----------|
| [CqrsMinimalApi](CqrsMinimalApi/) | [matjazbravc/CQRS.MinimalAPI.Demo](https://github.com/matjazbravc/CQRS.MinimalAPI.Demo) | Student CRUD — simplest MediatR → Wolverine port | Wolverine.HTTP, Marten documents, `[Entity]`, Alba tests |
| [CleanArchitectureTodos](CleanArchitectureTodos/) | [jasontaylordev/CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture) | Todo lists — Clean Architecture unraveling (67 files → 11) | FluentValidation middleware, `ValidateAsync`, one-file-per-request layout |
| [OutboxDemo](OutboxDemo/) | [MassTransit/Sample-Outbox](https://github.com/MassTransit/Sample-Outbox) | Registration workflow with transactional outbox and Saga | Marten outbox, Wolverine Saga, cascading messages, `Results.NoContent()` |
| [EcommerceMicroservices](EcommerceMicroservices/) | [aspnetrun/run-aspnetcore-microservices](https://github.com/aspnetrun/run-aspnetcore-microservices) | E-commerce with 4 services communicating via RabbitMQ | Wolverine RabbitMQ transport, per-service databases, `[Entity]` |
| [EcommerceModularMonolith](EcommerceModularMonolith/) | Same as above | Same domain collapsed into one app with durable local queues | Schema-per-module, durable local queues, same handler code as microservices |
| [MeetingGroupMonolith](MeetingGroupMonolith/) | [kgrzybek/modular-monolith-with-ddd](https://github.com/kgrzybek/modular-monolith-with-ddd) | Meeting group scheduling — 5 modules with event sourcing | Marten event store (Payments), durable local queues, inter-module events |
| [PaymentsMonolith](PaymentsMonolith/) | [devmentors/Inflow](https://github.com/devmentors/Inflow) | Virtual payments — 4 modules (Users, Customers, Wallets, Payments) | Schema-per-module, cascading events across modules, `ValidateAsync` |
| [BookingMonolith](BookingMonolith/) | [meysamhadeli/booking-modular-monolith](https://github.com/meysamhadeli/booking-modular-monolith) | Travel booking — replaces EventStoreDB + MongoDB with Marten | Marten event store, inline snapshots, multiple `[Entity]` batch loading |
| [BankAccountES](BankAccountES/) | Inspired by [andreschaffer/event-sourcing-cqrs-examples](https://github.com/andreschaffer/event-sourcing-cqrs-examples) | Bank accounts — store-agnostic event sourcing; runs on Marten (Postgres) or Fisher (SQLite) from one set of handlers | `[DeciderFunction]`, `[Entity]`, `Storage.StartStream`, `IEventStoreOperations` / `IDocumentReadOperations`, inline `Snapshot<T>` read model, `Validate` against aggregate state |
| [MoreSpeakers](MoreSpeakers/) | [cwoodruff/morespeakers-com](https://github.com/cwoodruff/morespeakers-com) | Speaker mentorship platform — Marten as document DB | Nested collections, multiple `[Entity]` batch queries, mentorship lifecycle |

## Common Patterns Across Samples

- **`IntegrateWithWolverine()` + `AutoApplyTransactions()`** — canonical Marten + Wolverine setup in every sample
- **`AddWolverineHttp()`** — required for Wolverine.HTTP endpoints
- **`[Entity]`** — declarative entity loading (Marten documents, event-sourced snapshots)
- **`[WriteAggregate]` + `IEventStream<T>`** — event-sourced aggregate mutations
- **`ValidateAsync` / `Validate`** — sad-path validation separated from happy-path handlers
- **`Results.NoContent()`** — preferred over `[EmptyResponse]` for 204 responses with cascading messages
- **FluentValidation** — `UseFluentValidationProblemDetailMiddleware()` in `MapWolverineEndpoints()`
- **Alba + Bobcat specs** -- xUnit v3 specs over `AlbaHost`, with `ResetEventStoresAsync()` for test isolation

## Running

Each sample has its own `.slnx` file and `Tests/` subfolder. Requires PostgreSQL (see
`docker-compose.yml` in this folder), except `BankAccountES` on Fisher, which needs nothing:

```bash
cd BankAccountES
dotnet test Tests/Tests.csproj                         # Marten, Postgres on 5433
EventStore=Fisher dotnet test Tests/Tests.csproj       # Fisher, a SQLite file, no docker
```

Every sample's specs are projected (code-first) xUnit v3 tests -- BankAccountES's
[README](BankAccountES/README.md#specifications) is the fullest worked example, slices and all.
