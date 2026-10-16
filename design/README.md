# Design records

**Not published.** The documentation site builds from `docs/` only; everything in this directory is
internal — written for whoever maintains Bobcat, not for whoever uses it.

The distinction is not about quality or staleness. It is about audience. A page belongs here when
it answers *"why is Bobcat shaped like this"* or *"what did we learn building it"*, and in `docs/`
when it answers *"how do I do the thing I came here to do."*

| | |
|---|---|
| `versions.md` | The canonical, mutually-compatible dependency set that `src/Directory.Packages.props` pins. Written around this repository's own constraints and its samples, not around a consumer's |
| `ledger-design.md` | Design of record for the committed test ledger — the store, the merge strategy, the aging rules |
| `wolverine-ci-rollout.md` | Lessons of record from the first production Supervisor rollout |
| `rider/` | The upstream Reqnroll.Rider patch, its PR body, and notes on what was and was not verified |
| `history/` | Gherkin-era records kept for history (the lane was removed in 0.50.0, bobcat#471): the original `spec-driven-development-design.md` vision, the `acceptance-coverage-map.md` from the lane's removal, and the `sample-wiring-playbook.md` for wiring samples to `BobcatRunner` |
| `storyteller-gap-analysis.md` | Living comparison of Storyteller 5 against Bobcat: the gap register, what was dropped and why, and an append-only log of decisions taken about each gap |
| `bdd-tools-comparison.md` | Testing strategies from Gauge, Reqnroll and Cucumber that a code-first Bobcat could use (no Gherkin or Markdown), with an append-only decision log |
| `ui-validation-560.md` | `bobcat ui` validated end to end on BankAccountES (#560): what works cold and warm, what was missing and filed, and how to repeat it |
| `critter-stack-interop-notes.md` | Upstream Wolverine/Marten/Fisher hazards a Bobcat suite was first to hit |
| `dogfood-critterstacksamples/` | bobcat#559: every CritterStackSamples test project adopted as a package consumer — the cross-project trends (`README.md`), one findings file per project, and the method |

## Known follow-up

Three pages still in `docs/` are **mixed** — genuinely user-facing material with an internal design
record embedded in it. They were left whole rather than split, because splitting them is an
editorial decision rather than a mechanical one:

- **`docs/editor-integration.md`** — the VS Code setup is user-facing; everything from "Rider —
  what blocks it" onward (the patch, options A and B, "How this was verified") is internal.
- **`docs/monitor-design.md`** — the wire contract is what an integrator needs; the decisions of
  record around it are not.

the design record moved here. `docs/sample-wiring.md` is done too — dissolved, with its playbook
here and its footguns distributed to the pages that own them.

A **user-facing** version-compatibility page is also missing now. `versions.md` moved here because
it is written about this repository's pins; a consumer still has the reasonable question of which
Bobcat works with which Marten and Wolverine, and nothing in `docs/` answers it.
