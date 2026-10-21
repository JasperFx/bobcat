# Querying Runs from the Command Line

**Everything the [Bobcat UI](bobcat-ui.md) shows is also readable as JSON from the `bobcat` tool.**
The commands are for scripts and, above all, for AI agents: an agent asks with a shell command and
reads JSON back, with no MCP client to configure and no tool schema taking up its context.

```bash
bobcat failing-tests            # what failed in the most recent run
bobcat failing-spec             # everything about the first failure: steps, cells, logs, exception
bobcat slice-coverage           # which Event Model slices have no spec, or a spec that never ran
```

Each command reads a **running** `bobcat ui`. The runs and the model live in its memory, so start one
first (`bobcat ui --no-browser` is enough for an agent).

## The commands

| Command | What it answers |
|---|---|
| `list-runs` | every run the UI knows, running, finished or orphaned, newest first |
| `run-status` | one run's progress: each scenario's outcome, the live steps of the one running, and for a supervised run its lanes, recycles, worker faults, stalls and latest heartbeat |
| `failing-tests` | every failed or aborted scenario in a run, with its error and failing steps. An empty list means nothing has failed so far |
| `failing-spec` | everything about one scenario: every step with its status, duration and error, its cells (the green ones too), logs, diagnostics, the exception with filtered frames, earlier attempts, the types it touched and the slices that bind it |
| `flaky-ledger` | scenarios that passed only after a retry, across every run the UI knows |
| `await-run` | wait for a run to finish, then print its summary -- instead of polling |
| `export-run` | a run's results as CTRF (the default) or JUnit XML |
| `event-model` | the Event Model the UI holds, or one slice or domain of it |
| `slice-coverage` | per slice, what is untested: **`no-spec`** (nothing bound) or **`no-evidence`** (bound, but no run has exercised it) |

## Options

Every command takes `-u, --url <url>`, the UI's base URL; `http://localhost:5525` by default.

| Option | Commands | What it does |
|---|---|---|
| `-r, --run <id>` | `run-status`, `failing-tests`, `failing-spec`, `export-run`, `await-run`, `flaky-ledger` | the run id, from `list-runs`. Defaults to the most recent run -- except for `flaky-ledger`, which reads every run unless narrowed, and `await-run`, described below |
| `--uid <identity>` | `failing-spec` | the `{Feature}/{Scenario}` identity; the run's first failing scenario by default |
| `--slice <name>` | `event-model` | only the slice with this name, case-insensitive |
| `--domain <name>` | `event-model` | only the slices in this domain, case-insensitive |
| `-f, --format <ctrf\|junit>` | `export-run` | `ctrf` by default |
| `-o, --output <file>` | `export-run` | write to a file instead of standard out |
| `-t, --timeout <seconds>` | `await-run` | how long to wait; 600 by default, at most 3600 |

### `await-run`

```bash
bobcat await-run --run 3b1f5f87-0152-41d2-a610-81fdeb3f919c --timeout 900
```

Without `--run` it waits on **the** run in flight. With nothing in flight it answers at once with the
most recent run -- a fast suite has often finished before anyone asks -- and with several in flight
it refuses and lists them, because guessing would latch onto somebody else's run. Pass the id from
`list-runs` whenever more than one thing on the box is testing.

It answers with `outcome` set to `finished`, `orphaned` (the run's process died), or `timeout` with the
progress so far.

## What comes back

JSON on standard out, camelCase, indented. These are real answers from a `bobcat ui` that three of
the sample corpus's classes had just published to, with one failure among them:

::: code-group

```json [failing-tests]
{
  "runId": "23284c18-28b1-423e-b89b-56d886e9fcee",
  "suite": "Bobcat.Xunit.Samples",
  "finished": true,
  "failing": [
    {
      "uid": "Actions/off stage setup never ran",
      "outcome": "Failed",
      "attempts": 1,
      "errorMessage": "2 specification steps failed:\n  Given The batch process has already run => The batch process has not been started\n  Then 12 records should have been processed => count: expected '12', got '0'",
      "failedSteps": [
        {
          "name": "Given The batch process has already run",
          "status": "Failed",
          "errorMessage": "The batch process has not been started",
          "failedCells": null,
          "exception": null
        },
        {
          "name": "Then 12 records should have been processed",
          "status": "Failed",
          "errorMessage": "count: expected '12', got '0'",
          "failedCells": [
            {
              "name": "count",
              "status": "failed",
              "expected": "12",
              "actual": "0"
            }
          ],
          "exception": null
        }
      ]
    }
  ]
}
```

```json [failing-spec]
{
  "runId": "23284c18-28b1-423e-b89b-56d886e9fcee",
  "suite": "Bobcat.Xunit.Samples",
  "uid": "Actions/off stage setup never ran",
  "feature": "Actions",
  "scenario": "off stage setup never ran",
  "status": "Failed",
  "attempts": 1,
  "durationMs": 1,
  "errorMessage": "2 specification steps failed:\n  Given The batch process has already run => The batch process has not been started\n  Then 12 records should have been processed => count: expected '12', got '0'",
  "finishedAt": "2026-10-21T17:17:42.971656+00:00",
  "steps": [
    {
      "name": "Given The batch process has already run",
      "status": "Failed",
      "durationMs": 0,
      "errorMessage": "The batch process has not been started",
      "cells": null,
      "logs": null,
      "diagnostics": null,
      "exception": null
    },
    {
      "name": "Then 12 records should have been processed",
      "status": "Failed",
      "durationMs": 0,
      "errorMessage": "count: expected '12', got '0'",
      "cells": [
        {
          "name": "count",
          "status": "failed",
          "expected": "12",
          "actual": "0",
          "note": null,
          "row": null
        }
      ],
      "logs": null,
      "diagnostics": null,
      "exception": null
    }
  ],
  "priorAttempts": [],
  "touchedTypes": [],
  "slices": []
}
```

```json [slice-coverage]
{
  "model": "CritterCrush",
  "summary": { "slices": 19, "noSpec": 2, "noEvidence": 17, "covered": 0 },
  "slices": [
    {
      "slice": "ProposeHomeCheckAppointment",
      "domain": null,
      "pattern": "Automation",
      "gap": "no-evidence",
      "specs": [
        {
          "identity": "ProposeHomeCheckAppointment/home check appointment proposed",
          "lastOutcome": null,
          "lastFinishedAt": null,
          "lastRunId": null,
          "lastSuite": null
        }
      ]
    }
  ]
}
```

```json [await-run]
{
  "outcome": "finished",
  "run": {
    "runId": "23284c18-28b1-423e-b89b-56d886e9fcee",
    "suite": "Bobcat.Xunit.Samples",
    "repository": "/Users/jeremymiller/code/bobcat",
    "repositorySlug": "JasperFx/bobcat",
    "branch": "docs-review-reference-pages",
    "mode": "xunit",
    "finished": true,
    "orphaned": false,
    "exitCode": 1,
    "startedAt": "2026-10-21T17:17:42.721092+00:00",
    "finishedAt": "2026-10-21T17:17:43.060574+00:00",
    "totalScenarios": null,
    "counts": {
      "completed": 9,
      "passed": 8,
      "failed": 1,
      "passedOnRetry": 0,
      "running": 0
    }
  }
}
```

:::

`event-model` returns the model in the shape the UI was given it: the `EventModelDescriptor` wire
format a Wolverine application's `event-model` command sends and the canvas draws.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | the answer is on standard out |
| `1` | the UI refused the question -- `{ "error": "…" }` on standard error, such as a run id that is not one, or no model pushed yet -- or nothing answered at `--url` |

Nothing answering reads like this, on standard error:

```
Nothing answered at http://localhost:5525. These commands read a running Bobcat UI — start one with `bobcat ui`, or check --url. (Connection refused (localhost:5525))
```

A UI that predates these commands -- a Stoat host, or a `bobcat ui` older than bobcat#524 -- answers
404, and the command says so rather than printing an HTML page.

## Using these from an AI agent

The loop for a red suite is three commands:

```bash
bobcat ui --no-browser &            # once, in the background
dotnet test --project src/MySpecs   # the run publishes to it
bobcat failing-spec                 # the first failure, in full
```

- **Read `failing-spec`, not the test log.** It carries every step's verdict and every cell, so
  `expected '12', got '0'` arrives as data rather than as a line to grep for, along with the touched
  types and the slices the scenario binds.
- **Park on `await-run` rather than polling** when something else started the run, and pass `--run`
  when more than one suite is running.
- **Check `flaky-ledger` before calling a suite green.** A pass on a retry still exits 0.
- **Use `slice-coverage` to choose the next slice.** `no-spec` means write the specification first;
  `no-evidence` means a specification exists and has not been run.

The [`critterstack-sdd-*` AI skills](https://ai-skills.jasperfx.net) describe the whole spec
driven loop these commands fit into; the authoring skill carries a reference on the UI and these
queries, and the troubleshooting skill tells an agent which one to reach for on a red run.
