# Validating `bobcat ui` in watch mode: recompile, and see the UI run and change (#561)

Run on 2026-10-16 against `samples/BankAccountES` (22 specifications) at 50947d2, on Fisher
(`EventStore=Fisher`, a private SQLite file per process), so no shared database was touched.
`bobcat ui` was built with `EmbedFrontend=true` and served on a private port with its own
`BOBCAT_MONITOR_DATA`. Two kinds of watch were tried, because #561 names both:

- **`bobcat watch samples/BankAccountES/Tests`**, publishing to the UI through `BOBCAT_MONITOR_URL`.
- **A resident runner relaunched on source change.** Bobcat has no relaunching parent of its own, so
  this was **`stoat runner`** (Stoat's tool: it rebuilds on a change, relaunches `bobcat resident`
  and reports `rebuilding` / `build-failed`), with `--url` at the UI, `--runner-id w561-runner` and
  `--bobcat` at this branch's `Bobcat.Cli`. Commands went through
  `POST /api/runners/{id}/commands`, the call the Run button makes.

Each change below was made to the sample in place and reverted afterwards.

## Verdict

**Recompiling and re-running work in both kinds of watch. Two things #561 asks for are missing:**
`bobcat watch` never tells the UI the build failed, and a relaunched resident runner triggers no
run. Both are filed.

| Change | `bobcat watch` | Resident runner under `stoat runner` |
|---|---|---|
| Application code (deposit adds the amount twice) | Rebuilt and reran on its own. A new card: 21 passed, 1 failed. `failing-tests` names `deposit funds` with both cells (`NewBalance` 100 vs 200, `Balance` 100 vs 200). Reverting it gave a green card | Relaunched under the same id. A warm command went red the same way (22 passed, 1 failed) |
| Add a spec (`Watch Probe`) | Next run 23/23. The canvas binds the new identity to `FreezeAccount` | Re-registered as `w561-runner` with 23 specs, still **one** runner entry. **Nothing ran** until a command was sent: then 23/23 cold and warm |
| Rename that spec | The run carries the new identity. The canvas drops the old one and binds the new one | Re-registered (count unchanged). Sending the old identity is **refused by name** at enqueue, but the message blames "another suite". **The canvas keeps the old identity** until a run republishes the spec half |
| Remove that spec | Next run 22/22, and the canvas no longer has it | (covered by rename) |
| Break the build (`+;`) | Terminal prints CS1525 and waits. **The UI gets nothing.** The board's top card still reads "22 passed" | `GET /api/runners` says `connected: false`, `status: build-failed`, with the compiler error as `statusDetail`. The page shows "build failed" **only in the Run dialog's runner picker**, and the error only as a hover tooltip. The board still leads with "23 passed". A command sent while broken was queued, then ran (23/23) once the build was fixed |
| Stable runner id across relaunches (#397) | n/a | Held across five relaunches (six registrations), with `registeredAt` moving and never a second entry |

Filed:

- **#651**: the UI doesn't say a watched project no longer compiles. Nothing at all from
  `bobcat watch`, and only inside the Run dialog under `stoat runner`. Bobcat has no relaunching
  parent of its own to report it.
- **#652**: after a resident runner relaunches, nothing runs and the board keeps the old code's
  verdicts unmarked. The canvas keeps renamed-away identities (fold into #644's
  publish-on-registration). A renamed spec's refusal says it belongs to another suite.

Already filed by #560 and seen again here: #644 (the runner's specs reach the UI only as a count, so
an added or renamed spec isn't visible until a run) and #646 (cards don't say what produced them).
Here every watch rerun and every command is an identical card in one flat list, and ten reruns
fill the board's default retention.

## Method, so it can be repeated

1. `./build.sh UiContracts`, then `dotnet build src/Bobcat.Console -p:EmbedFrontend=true`, then
   `dotnet build samples/BankAccountES/BankAccountES.slnx`.
2. `BOBCAT_MONITOR_DATA=<tmp> Bobcat.Cli ui --port <free> --no-browser`.
3. With `EventStore=Fisher`, `ConnectionStrings__Fisher="Data Source=<tmp>/bank.db"` and
   `BOBCAT_MONITOR_URL=http://localhost:<port>` exported:
   `Bobcat.Cli watch samples/BankAccountES/Tests < /dev/null`. Make each change, wait for the
   rerun, and read `GET /api/runs`, `/api/agent/failing-tests` and `/api/event-model`.
4. Stop it. Then, with a different SQLite file:
   `stoat runner samples/BankAccountES/Tests --url http://localhost:<port> --runner-id <id> --agent-worktree --bobcat <…>/Bobcat.Cli`.
   Make each change, read `GET /api/runners`, and send commands to
   `POST /api/runners/<id>/commands` with `{"specs": [...], "mode": "cold"|"warm"}`.
5. With the build broken, open a spec page in Chromium and press **Run again**. The dialog is the
   one place "build failed" shows.

Not covered: TUnit, NUnit or MSTest suites in watch mode, and a long suite where a change lands
while a run is in flight.
