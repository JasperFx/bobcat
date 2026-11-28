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

## `dcb/` — the dcb.events Playground model (`model/v10`, bobcat#849)

| File | Source | Commit | Licence |
|---|---|---|---|
| `dcb-model.schema.json` | https://github.com/dcb-events/dcb-playground (`dcb-model.schema.json`, `$id` `https://dcb.events/schemas/model/v10.json`, revision 10.1.0) | `b937e55322239a75cace670ed12415e17f36646e` (2026-11-27) | MIT (`LICENSE`, copied from the same repo) |
| `examples/*.dcb.json` (nine files) | https://dcb.events/examples/ — the model behind each worked example, as the page's "open in the playground" link carries it (`#model=`, gzip + base64url of the envelope), decoded and pretty-printed; the last model on each page, which is the complete one. Website source https://github.com/dcb-events/dcb-events.github.io | `56378a4175022b3cccd69ac05d2f44fe7cfda655` (2026-11-27; pages fetched the same day) | **CC BY-SA 4.0** (the site's `LICENSE` puts everything under `docs/` under it; the Playground's own code is MIT) |

The ten examples the issue names are nine files: the tenth, *event-sourced aggregate*, is a JavaScript walkthrough with
no Playground model on its page. `library-loans.dcb.json` carries one handler whose value is `null` (`set null`), which
the schema's `HandlerOperand` does not admit; `DcbPlaygroundReaderTests` records that as a known deviation rather than
editing the file.

<!-- JEREMY: a third licence question. The nine example models are decoded from the dcb.events documentation pages,
which are CC BY-SA 4.0, not MIT (only the Playground's code is MIT). They are test inputs, attributed above, never
shipped in a package, but a share-alike licence inside an MIT repository is your call. The alternative is the
Playground repository's own `fixtures/*.dcb.json` (MIT; course-simple, course-sequence, developer-hours and five
content-decisions variants), which are not the ten the issue names. -->
