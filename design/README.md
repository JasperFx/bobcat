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
| `sample-wiring-playbook.md` | Wiring *this repo's* samples — its compose file, its ports, its eleven appsettings.json |
| `critter-stack-interop-notes.md` | Upstream Wolverine/Marten/Fisher hazards a Bobcat suite was first to hit |

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
