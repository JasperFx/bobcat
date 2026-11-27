# Vendored conformance fixtures (bobcat#847)

Published inputs the importer is run against by `VendoredFixtureTests`, so the Emlang and EM Spec
readers are proven against what the tools actually write rather than against hand-written stand-ins.
**Everything under this folder is untrusted data**: the tests read it; nothing executes it, and nothing
here is compiled (`Fixtures/**` is excluded from compilation in the test project).

Each file is copied byte for byte from the commit named below. Refresh one by copying the file again
and updating its row here.

## `emlang/` — the Emlang Project (community grammar)

| File | Source | Commit | Licence |
|---|---|---|---|
| `schema.json` | https://github.com/emlang-project/spec (`schema.json`, spec v1.0.0) | `cd876dea5210a98f5198cc23265faa143ca5a249` (2026-02-05) | MIT (`LICENSE`, copied from the same repo) |
| `examples/brainstorming.yaml`, `examples/pizza.yaml`, `examples/user-registration.yaml`, `examples/ux-modeling.yaml` | https://github.com/emlang-project/examples | `ad624b73fd3af2febec0883c2ff7b5f525f2e653` (2026-11-20) | MIT |

The toolchain (https://github.com/emlang-project/emlang, v1.4.0, commit
`4117ff0fab51a0210454f11e2fb3a988132a99fd`) ships no example files of its own; its lint rules
(`command-without-event`, `view-without-event`, `orphan-exception`, `trigger-without-command`) are
mirrored as import callouts of the same names.

## `emspec/` — EM Spec JSON (eventmodelers.ai's own export)

| File | Source | Commit | Licence |
|---|---|---|---|
| `nebulit-demo/config.json`, `nebulit-demo/submitcart.slice.json` | https://github.com/Nebulit-GmbH/Eventmodelers-Build-Kits, `eventmodelers-cli/shared/demo-slices/Understanding Eventsourcing/` (the 16-slice board `eventmodelers init --demo` seeds; `@eventmodelers/cli` 2.0.85) | `f483d9813fef89424eca49b66b5f203026b68e6b` (2026-11-26) | CC BY-NC 4.0 (`LICENSE`, copied from the repo root) |
| `eventsourcing-book/config.json` | https://github.com/dilgerma/eventsourcing-book (the board behind *Understanding Eventsourcing*) | `82e927b44cc266d3f3e51f3dd6e9c9507dc15468` (2025-07-23) | **No licence file in the repository** (GitHub reports NOASSERTION) |

<!-- JEREMY: two licence questions for your read before 1.2 ships. (1) The Nebulit demo board is
CC BY-NC 4.0, not MIT: attribution is given above and it is used only as a test input, never shipped
in a package, but it is a non-commercial licence inside an MIT repository. (2) The eventsourcing-book
repository carries no licence at all, so this copy rests on nothing but the author having published
it; if you would rather not carry it, delete the file and the one theory row that names it in
VendoredFixtureTests. -->
