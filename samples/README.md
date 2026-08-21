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

Samples wired to Bobcat carry a `Tests/` subdirectory that runs their `.feature` files:

```bash
cd PaymentsMonolith/Tests
dotnet run -- list     # discovered features and scenarios
dotnet run -- run      # execute them
```

See [docs/sample-wiring.md](../docs/sample-wiring.md) for how to wire one that is not yet wired.

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
- **Alba + Shouldly** — integration tests with `CleanAllMartenDataAsync()` for test isolation

## Running

Each sample has its own `.sln` file and `Tests/` subfolder. Requires PostgreSQL (see
`docker-compose.yml` in this folder), except `BankAccountES` on Fisher, which needs nothing:

```bash
cd BankAccountES
dotnet run --project Tests -- run                      # Marten, Postgres on 5433
EventStore=Fisher dotnet run --project Tests -- run    # Fisher, a SQLite file, no docker
```
