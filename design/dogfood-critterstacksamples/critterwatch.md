# critterwatch (Embedded.Sqlite, WebService.Http, Fleet.*)
Seven test projects plus a shared harness (`Shared/CritterWatch.Samples.Testing`, Aspire.Hosting.Testing). Two very different shapes.

## Embedded.Sqlite (done fully)
- Tests before: 6 (`InventoryIsolationTests.cs`, xUnit v2 + VSTest + `Microsoft.AspNetCore.Mvc.Testing` WebApplicationFactory + Shouldly).
- Specs after: 7 on `WolverineHttpSpec` (one test split): **7 passing / 0 failing / 0 skipped**.
- App: an inventory service on Fisher (SQLite) with **embedded CritterWatch** (`AddCritterWatchEmbedded`), i.e. an ancillary
  console store and a host that is never quiet.

### Adoption steps
- The sample is a CPM island (`Directory.Packages.props`), so the test stack moved there: xunit.v3 4.0.2, Bobcat.Xunit/Generators 0.60.4,
  WolverineFx.Bobcat(.Http) 6.51.0-alpha.1, `Microsoft.Data.Sqlite` (for the table checks). CritterWatch `1.1.0-embedded.10` from a local
  folder feed -> `1.1.0-rc.1` from nuget.org, so the sample's `nuget.config` feed + source mapping could go.
- WebApplicationFactory -> Alba `AlbaHost.For<Program>` (WolverineHttpSpec wants an `IAlbaHost`); `opts.ApplicationAssembly` set.
- `[assembly: EventModelName("InventoryService")]` — the app sets no `ServiceName`, so Wolverine's default is the entry assembly name;
  the skill should say what to put when there is no ServiceName.
- The skill's "a host that is never quiet (embedded CritterWatch)" recipe (`opts.Tracking.IgnoreMessagesFromAssembly(...)` for the two
  CritterWatch assemblies) was applied verbatim and works. **Without it the suite also passed in the same time (1.3 s)** on this
  sample, so the recipe could not be shown to be necessary here (the console's traffic settled within the tracked session's window).
- **The main-store default worked**: Fisher main store + CritterWatch's ancillary store, and every unqualified `GivenReadModel` /
  `ThenDocument` went to the host's store with no `In<T>()` and no refusal (the bobcat#504 C fix, as published in 0.60.4).

### Diagnostics, surprising renders
- **`WhenReading<object>("/critterwatch")` against an HTML page is an error, not a read**:
  `System.Text.Json.JsonException : '<' is an invalid start of a value.` — every `WhenReading<T>` deserializes JSON, so there is no way to
  GET a non-JSON resource (a SPA shell, a file, a text endpoint) through the vocabulary. Worked around with raw `Host.Scenario(...)`.
  Also: the failure's HTTP-exchange table in the runner's plain-text output printed the **entire HTML body** in the response column
  (several hundred characters of padding per row) — needs a cap like the reports' 200 rows.
- The run summary flagged `⚠ 1 scenario(s) ran steps but asserted nothing • the consoles SPA is actually mounted` while that scenario
  was erroring — correct but confusing next to the error.
- `GivenReadModel<Product>(…)` rendered the values here (`Product(Id: "widget-…", Name: "Widget", OnHand: 5)`) because `Product` is a
  record with a generated `ToString`; for a class (BookingMonolith, PaymentsMonolith) the same step renders only the type name.

### Event Model and manifest
- Manifest: 7 identities. `ReceiveStock`: Command, POST `/inventory/receive` (constant route + endpoint index gave the verb). `GET /inventory/{id}`
  (explicit `SliceName`): Query with route and verb. The `WhenReading<object>` specs (console API, 404 probe) stamp nothing — right, there is nothing to stamp.

## Fleet.* (5) and WebService.Http (assessed; Fleet.PostgresqlQueues converted and built)
- Tests before: 2 per Fleet solution, 4 in WebService.Http — smoke batteries over `Aspire.Hosting.Testing`: boot an AppHost (containers for
  Postgres / RabbitMQ / Service Bus emulator / Pub/Sub emulator / SQL Server), then poll the console over a plain `HttpClient`.
- **Does Bobcat fit?** Only its recording half. The system under test is several processes, so there is no in-process `IHost`/`IAlbaHost`
  for `CritterStackSpec`, `WolverineSpec` or `WolverineHttpSpec`: none of the Critter Stack vocabulary applies, there is no tracked session,
  and with no `JasperFx.Events` reference no Event Model is generated. What works: `[BobcatSpec]` + `[Given]/[When]/[Then]` step helpers + `SpecAssert`.
- Converted `Fleet.PostgresqlQueues/Tests` that way (the shared harness moved from `xunit` to `xunit.v3.extensibility.core`, its
  `IAsyncLifetime` from `Task` to `ValueTask`; the CPM file gained xunit.v3/Bobcat; `Microsoft.NET.Test.Sdk` 18.0.1 -> 17.14.1 to match the rest).
  **Builds clean; `--list-tests` and `BOBCAT_LIST_SPECS` list both specs with the expected identities; interceptors generated.** Not run:
  the AppHost starts containers, which this dogfood run must not do.
- Gaps an Aspire suite would want: a polling/eventually step ("within 2 minutes, X") with the attempts rendered (the harness's
  `WaitForServicesAsync` loop is invisible to the render); an `HttpClient`-based HTTP vocabulary (`WhenReading`/`ThenResponseIs` without Alba)
  so the HTTP exchange table works for out-of-process hosts too.
- The shared-harness conversion is the real cost: one library consumed by six solutions has to move to xUnit v3 for any of them to adopt Bobcat.

## Candidate issues
- WolverineFx.Bobcat: a non-JSON read (`WhenReadingText(route)` or `WhenReading<string>` returning the raw body) — Embedded.Sqlite's SPA check.
- Bobcat (rendering): cap the response column of the HTTP-exchanges table in plain-text output.
- Bobcat / WolverineFx.Bobcat: an HttpClient-only HTTP vocabulary + an "eventually" step for out-of-process (Aspire) suites — a design question, not a bug.
- ai-skills: say what Bobcat offers an Aspire.Hosting.Testing suite (recording only) and what `EventModelName` is when the app sets no ServiceName.
