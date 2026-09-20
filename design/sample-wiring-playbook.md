# Sample Wiring Playbook (internal)

**Not published.** How to wire *this repository's* samples to `BobcatRunner` so their `.feature`
specs run end-to-end through Alba — the canonical reference for issue #8, with
`samples/CqrsMinimalApi/Tests/` as the reference implementation.

It is internal because it is about `samples/`: this repo's compose file, this repo's database
ports, this repo's eleven `appsettings.json` files. A consumer wiring their own host wants
[Resources](../docs/resources.md) and the integration pages, which is where the footguns this
playbook uncovered now live. The upstream-framework ones are in
[critter-stack-interop-notes.md](critter-stack-interop-notes.md).

## Before anything: start the database

```bash
cd samples && docker compose up -d
```

Published on **5433**, which is the port every sample's default connection string already names,
with one database per sample created by `samples/init/create-databases.sql`. This did not exist
until `PaymentsMonolith` was wired, and its absence is not a footnote — the whole lesson of this
playbook is that **a sample is not fixed until it has been run**, and there was nothing to run it
against. Wiring a sample without starting this is wiring it blind.

**5433 collides with the Wolverine repo's own Postgres**, which publishes on the same port and is
routinely left running for days. `docker compose up -d` then fails with `Bind for 0.0.0.0:5433
failed: port is already allocated`, and killing the other repo's container to get your own is the
wrong trade. Bobcat's *root* `docker-compose.yml` already learned this and sits on 5445 precisely
so it never collides. The fastest way through, since it is the same image and the same
`postgres`/`postgres` credentials, is to create the sample databases in whichever instance holds
the port:

```bash
for db in bank_account booking clean_architecture_todos cqrs_minimal_api ecommerce \
          inflow meeting_groups more_speakers outbox_demo; do
  docker exec <container> psql -U postgres -c "CREATE DATABASE $db"
done
```

Moving `samples/docker-compose.yml` off 5433 is the durable fix, but it means editing the
connection string in all eleven `appsettings.json` files, so it is a decision rather than a
detail.

## The playbook

For each sample, replicate what `CqrsMinimalApi` has:

1. **Add a `Tests/` subdirectory** with three files:
   - `Tests.csproj` — `net10.0`, `OutputType=Exe`; project-references the host + `Bobcat` +
     `Bobcat.Alba` + `Bobcat.Generators` (as an analyzer); `<Compile Include="..\<Project>Fixture.cs" />`
     to link the fixture in; `<AssemblyName><Project>.Tests</AssemblyName>` to match
     `[InternalsVisibleTo]`.
   - `SpecsRunner.cs` — an **explicit `static class SpecsRunner` with a `Main`**. Do **not** use
     top-level statements (see footgun #1).
   - `AssemblyAttributes.cs` —
     `[assembly: WebApplicationFactoryContentRoot("<HostAssemblyName>", "../../../..", "appsettings.json", "1")]`
     so Alba can find the host's content root despite the nested layout (footgun #2).
2. **Update the host `.csproj`:** target `net10.0` and the canonical package set, which is pinned
   centrally in `src/Directory.Packages.props` — do not restate the versions here, since a second
   copy is a second thing to drift.
   - Bump `TargetFramework` to `net10.0` if still on `net9.0` (footgun #1 also presents as a TFM mismatch).
   - `<InternalsVisibleTo Include="<Project>.Tests" />`.
   - Exclude the fixture from the host compile group: `<Compile Remove="<Project>Fixture.cs" />`.
3. Make the fixture extend `Bobcat.Fixture` and use `Context!` (not a stored field), or take an
   `IStepContext` parameter per step. **`Fixture` is not optional** — the generator's fixture
   discovery is `inheritsFrom(symbol, "Bobcat.Fixture")`, so a class that merely carries
   `[FixtureTitle]` matches nothing and the feature generates no code at all. The symptom is
   silence, not an error: the project compiles, and `list` reports no features.
4. **Give the resource a reset hook if the host has persistent state.** `AlbaResource`'s `reset:`
   parameter is `ResetBetweenScenarios`. A suite that passes once per database and then reports
   conflicts for records it believes are new is worse than no suite — and it is the default
   outcome for any sample with a unique index. `samples/OutboxDemo/Tests/SpecsRunner.cs` is the
   worked example (`store.Advanced.Clean.DeleteAllDocumentsAsync()`).
5. **Bring the host API and the fixture into agreement.** This is where the work usually goes —
   fixtures often describe a clean RESTful contract while host endpoints are RPC-style. Refactor
   the host (Path A) rather than weakening the spec. Expect the fixture to describe endpoints
   that do not exist at all: `OutboxDemo`'s posted to `/api/meetings/member-joined` while the host
   exposed one `POST /registration`. Nothing had ever compiled it, so nothing reported the drift.
6. Drop and recreate the host's Marten schema before the first run (old shape may conflict).
7. **Wait for cascaded messages before asserting** if the host routes integration events between
   modules. See footgun 7 — this is the difference between a suite that passes and one that
   passes *reliably*, and it does not announce itself.
8. **Run it twice, then break it once.** Twice, because persistent state is what a first run
   cannot reveal. Broken once, because a spec that cannot go red has told you nothing: change an
   expected value, confirm the failure lands on the step you expected, change it back.
   `PaymentsMonolith` was verified this way, including removing the cascade tracking to confirm
   three scenarios really do fail without it.


## Fixing a drifted sample

Two things that come up every time a sample's fixture is brought back into agreement with its
host. Both are about `samples/`, which is why they live here.

### Expect read endpoints to be missing entirely
Drifted fixtures describe *writes* that were at least plausible, but the assertion side often has
nothing to call. `PaymentsMonolith` had no `GET /api/customers/{id}` at all — the module could
only be written to, so the sample's central claim (registering a user creates a customer stub)
was unobservable. Path A applies: add the endpoint to the host rather than dropping the
assertion. It is usually four lines.

### A cascade that mints its own id is unobservable to the caller
In a modular monolith the interesting write is usually the *second* one — the record another
module creates in response to the first. If that handler does `Id = Guid.NewGuid()`, nobody
outside the process can address what it made: `MeetingGroupMonolith`'s accepted proposal created
a `MeetingGroup` under a fresh Guid, so "accepting the proposal creates the group" could only be
checked by searching the whole list for a matching name. Path A applies: give the created record
the id the caller already holds (the group takes the proposal's id, which is what the original
project did too), and footgun 9's read endpoint then has something to read. Same shape in the
Payments direction — a subscription's cascade is only observable because the `Member` it updates
carries the user's id.

