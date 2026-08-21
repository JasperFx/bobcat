# Handoff: Bobcat.Monitor (2026-07-31, one session, design → shipped tool)

Start here, then `docs/monitor-design.md` (all decisions of record, kept current), then
memory `project_monitor.md`. This doc is the session-specific state the repo doesn't carry.

## Where things stand

Built and merged to main **in this order** (each PR message is a good mini-doc):

| PR | What |
|----|------|
| #66 | Scaffold: Wolverine+SignalR host (`src/Bobcat.Monitor`), Vue/Pinia/Element Plus SPA (`src/Bobcat.Monitor.FrontEnd`), vitest CI gate (`monitor-frontend.yml`) |
| #69 | Issue #65: `CompositeObserver` + `BobcatRunner.AddObserver`, run bracket on `IExecutionObserver`, dependency-free publisher (`src/Bobcat/Monitoring/`), auto-on in real entry points only |
| #71 | Live SignalR wire verified; CloudEvents-envelope-as-string pinned as a vitest regression test; Wolverine version split collapsed |
| #74 | NDJSON archive + registry (`~/.bobcat/monitor/runs`), CTRF/JUnit/NDJSON eject endpoints + UI links, `[NotBody]` DELETE fix |
| #76 | Hydration both ways (boot rehydrate w/ orphan marking, eject moves to `ejected/`; browser replays archives through `relayToStore`), heartbeat-drain race fix |
| #77 | MCP at `/api/mcp`: `list_runs`, `run_status`, `failing_tests`, `flaky_ledger`, `export_run`, `await_run_completion`; locked registry reads |

**In flight**: [PR #78](https://github.com/JasperFx/bobcat/pull/78)
(`feature/monitor-embedded-spa`, commit 5b2cb78, **CI green**) — `EmbedFrontend` packaging so
`dotnet tool install bobcat-monitor` ships the whole console. Fully verified locally
including a real `dotnet tool install` from a local nupkg. Just needs Jeremy's merge.

## Working environment

- Worktree: `.claude/worktrees/bobcat-monitor` (this session's home; safe to delete once the
  in-flight PR merges — everything is pushed).
- **Merge-state gotcha that bit twice**: Jeremy twice said "merged NN" while the PR was still
  open (#71, #77 — both times he'd merged a *different* PR). Always
  `gh pr view NN --json state` before branching off `origin/main`; merge it yourself if his
  intent is clear (that was the established pattern this session).
- Background `npm`/`vite`/`dotnet run` processes started via Bash get reaped between turns
  sometimes — recheck with curl before assuming a server is still up. The Browser pane
  screenshot glitches after JS-triggered reloads; `get_page_text` still works, or reopen the
  pane.
- CI: `tests.yml` (dotnet, push), `monitor-frontend.yml` (path-filtered vue-tsc+vitest),
  `build` (appeared mid-session from another merge), `publish.yml` (tag-triggered; now packs
  the monitor explicitly with `EmbedFrontend=true` + node).

## How to run it

```bash
# Dev loop: host on 5525, Vite on 5173 proxying /api (ws included)
dotnet run --project src/Bobcat.Monitor
cd src/Bobcat.Monitor.FrontEnd && npm run dev

# Feed it anything — ConsolePreview auto-publishes via BobcatRunner.Run:
dotnet run --project src/ConsolePreview/ -- run

# The packed tool (frontend embedded):
dotnet pack src/Bobcat.Monitor/Bobcat.Monitor.csproj -c Release -p:EmbedFrontend=true -o /tmp/nupkg
dotnet tool install --tool-path /tmp/tools --add-source /tmp/nupkg Bobcat.Monitor
ASPNETCORE_URLS=http://localhost:5525 /tmp/tools/bobcat-monitor
```

Env knobs: `BOBCAT_MONITOR_URL` (publisher target), `BOBCAT_MONITOR=0` (kill switch),
`BOBCAT_RUN_ID` (run-identity passthrough, unused until supervisor sets it),
`BOBCAT_MONITOR_DATA` (archive dir).

## Hard-won facts (all encoded as code comments/tests, listed here for speed)

- `WebSocketMessage` marker lives in core `Wolverine`, not `Wolverine.SignalR`.
- Wolverine.SignalR delivers the browser a CloudEvents envelope **as a JSON string**; `type` =
  snake_case message name, `data` = event. Pinned verbatim in `relayToStore.test.ts`.
- Wolverine.HTTP binds the first complex param of a bodyless DELETE as the body → `[NotBody]`.
- Wolverine 6.24 needs `WolverineFx.RuntimeCompilation` or the host dies at startup.
- MSBuild embedding: static glob evaluates before targets run; `%()` on globbed includes
  inside targets batches. Items must be created in-target via glob-then-transform
  (`Bobcat.Monitor.csproj`, BuildFrontend target).
- `Timer.Dispose()` doesn't drain in-flight callbacks — the observer uses `Dispose(WaitHandle)`.
- The wire contracts are deliberately duplicated (Bobcat.Monitoring mirrors ↔
  Bobcat.Monitor.Contracts); `ContractRoundTripTests` is what keeps them honest. Don't "fix"
  the duplication.
- Jeremy moved `DispositionKind` etc. into JasperFx (`JasperFx.Testing` namespace, issue #63)
  mid-session; projects using it need a direct JasperFx PackageReference (CPM doesn't pin
  transitives — see the comment in Bobcat.Monitor.csproj).

## Next work, in the order I'd do it

1. ~~Merge the embedded-SPA PR if green (then the worktree can go).~~ DONE — #78 merged,
   worktree removed (later session, same day).
2. ~~Server-side event batching + archive retention/aging~~ DONE — PR #79 (same-day follow-up
   session). Bonus fix in it: rAF flush stalls in hidden tabs (rAF never fires) — plain-timer
   backstop in useSignalR.ts. Beware stray `src/**/*.js` from running `vue-tsc -b` before
   `npm ci`: Vite serves the stale .js over the .ts and edits silently stop applying.
3. ~~CTRF `retryAttempts[]` detail~~ DONE — PR #80 (merged). `ScenarioProjection.PriorAttempts`
   snapshots each retried-away attempt (on RetryScheduled with disposition/reason, or next
   attempt's start as fallback); export ships the FULL attempt list incl. the final one, steps
   in each attempt's `extra`. Schema-validated against ctrf-io/ctrf, which also caught `suite`
   must be an ARRAY (fixed).
4. TS codegen lift (NJsonSchema, CritterWatch's GenerateCommand pattern) once contracts grow.
5. ~~Supervisor sets `BOBCAT_RUN_ID` for workers~~ DONE — PR #83. `Supervisor.PublishToMonitor`
   (opt-in) + `SupervisorRunPublisher` own the bracket; workers get `BOBCAT_RUN_ID` +
   `BOBCAT_RUN_OWNER` via `WorkerLaunchContext.Environment` and suppress their own bracket.
   Verified live: 4 workers → 1 card. Still open: `ISupervisorObserver` for lane/retry
   topology (`MtpWorkerClient.handleNotification` already receives per-test updates and
   discards them; a supervised retry re-streams as attempt 1 with no RetryScheduled).
6. Dogfood the Gherkin runner against this UI as its first e2e (replaces Playwright,
   deliberately). Then the rename plan: this tool becomes "Bobcat", the library gets renamed.
