# bobcat
Integration testing tooling

## Docs
- [Sample Wiring Playbook](docs/sample-wiring.md) — how to wire a sample host to `BobcatRunner`
  so its `.feature` specs run end-to-end through Alba, plus the known wiring footguns and fixes.
- [Version Matrix](docs/versions.md) — the canonical, mutually-compatible dependency set
  (WolverineFx / Marten / JasperFx / Alba / TFM) that Bobcat and its samples align on.
- [Editor Integration](docs/editor-integration.md) — step completion and go-to-definition for
  `.feature` files: VS Code works today with the official Cucumber extension and the committed
  `.vscode/settings.json`; Rider needs an upstream `Reqnroll.Rider` change (proposal inside).
