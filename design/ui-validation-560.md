# Validating `bobcat ui` end to end: preview, then run a spec, a suite, a slice, the project (#560)

Run on 2026-10-16 against `samples/BankAccountES` (22 specifications) at b917271, on Fisher
(`EventStore=Fisher`, a private SQLite file), so no shared database was touched. `bobcat ui` was
built with `EmbedFrontend=true` and served on a private port with its own `BOBCAT_MONITOR_DATA`.
`bobcat resident <BankAccountES.Tests> --url <that port>` was attached, offering cold and warm.

## Verdict

**What exists works, cold and warm, and matches `bobcat run`.** What #560's scenario asks for
*beyond* that does not exist yet, and is filed:

| #560 step | Result |
|---|---|
| Preview the project's specifications in the UI, without running | **Not possible.** Before any run, the board is empty and the canvas has no model (`GET /api/event-model` 404s). The runner's 22 identities reach the UI only as a count. Filed as **#644** |
| Run one specification from the UI | Works: spec page, **Run again**, cold and warm |
| Run one suite (feature/class) | Works: spec page, **Run *Feature***, 9 specs, cold and warm |
| Run one slice (C5) | Works: canvas drawer, **Run this slice** (WithdrawFunds, 2 specs), cold and warm |
| Run the whole project | **No button.** The command API refuses an empty selection, and a run page with no failures has no run button at all. Posting all 22 identities to `POST /api/runners/{id}/commands` (what a button does) works, cold and warm. Filed as **#645**, along with "nothing to press until a run exists" |
| Live on the board, steps/cells/verdicts | Yes. Each command's run appears, links back from the run dialog, and renders steps, cells and verdicts |
| Same as `bobcat run` for the same selection | Yes. For `--scenario "deposit funds"`, `--feature "Bank Account Event Sourcing"`, `--slice WithdrawFunds` and the whole project, the scenario set, step text, step status and every cell's column/expected/actual (66 cells across the project) are identical between the CLI's results file and the UI's run, for cold, warm, and a second warm run into the same host |

Board-level papercuts found along the way are filed as **#646**:

- A card doesn't say what a commanded run selected, or cold vs warm (both show `xunit`).
- The 10-runs-per-job board retention evicts the page you pressed from after a few presses.
- `totalScenarios` is always null.

## Method, so it can be repeated

1. `./build.sh UiContracts`, then `dotnet build src/Bobcat.Console -p:EmbedFrontend=true`, then `dotnet build samples/BankAccountES/BankAccountES.slnx`.
2. `BOBCAT_MONITOR_DATA=<tmp> Bobcat.Cli ui --port <free> --no-browser`.
3. With `EventStore=Fisher` and `ConnectionStrings__Fisher="Data Source=<tmp>/bank.db"` exported, run
   `Bobcat.Cli resident <…>/BankAccountES.Tests --url http://localhost:<port> --id <id>`.
4. Drive the buttons in Chromium. A Playwright script opened a spec page and the canvas
   (`/event-model?spec=…` opens the slice drawer), pressed each button, chose Cold or Warm, sent
   it, and followed **open the run**. Scope the `run-link` locator to the dialog: the spec page's
   breadcrumb carries the same test id.
5. Run `bobcat run --json` with the same selections, `BOBCAT_MONITOR_URL` pointed at the same UI,
   and compare each results file with `GET /api/runs/{runId}` for the commanded run.

Not covered: a *failing* specification. Every BankAccountES spec is green, so red cells were
compared only through the UI's own tests, not this pass.
