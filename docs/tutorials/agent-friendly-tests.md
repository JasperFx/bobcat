# Agent Friendly Integration Tests

::: warning OUTLINE -- NOT YET WRITTEN
This tutorial has no prose yet. The outline below records the argument it needs to make, and the
Bobcat features it would draw on, each linked to the page that documents it.
:::

## What this tutorial is for

A coding agent iterating on a failing integration test is working from whatever the failure said.
When that is `Expected True but was False`, the agent's next move is to add logging and run again  -- 
and the loop costs a round trip that a better failure would have saved.

The thesis: **a test should write out what the system actually did, not only whether it matched.**
Integration tests have that information and usually throw it away.

## To cover

### 1. The argument, concretely

A before-and-after on one failure: the same broken behaviour reported two ways, and what each one
lets an agent do next. This is the section that has to land -- the rest is mechanism.

### 2. The worked example: Wolverine's `TrackedSession`

This is the anchor case. A tracked session knows the whole messaging story -- what was sent, what
was handled, in what order, and what threw -- and a test that merely asserts an outcome discards all
of it.

- For Critter Stack applications, WolverineFx.Bobcat surfaces the tracked session to a
  specification -- see [Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat) in the Wolverine docs.
- The session's own diagnostics -- `Status`, `AllExceptions()`, `AllRecordsInOrder()` -- are the
  material. `AllRecordsInOrder()` in particular is a causal chain, which is exactly what an agent
  cannot reconstruct from an assertion message.
- Wolverine's own `AssertCondition` throws *with* the history attached, which is the pattern to
  generalize from.

### 3. The vehicles Bobcat already gives you

The point is not to print more to the console. It is to put the context somewhere structured:

- **Expected/actual cells** -- `SpecAssert.Check` and every `Then…` in the Critter Stack vocabulary
  report the value the system actually had, not only that it differed.
- **Set verification reporting** already says which rows were missing, extra or wrong, rather than
  that the sets differed.
- **Step logs** -- `SpecLog.Write("…")` records a line on the step in progress, kept on a failure (or
  with `--verbose`) and shown under that step ([Saying what the system did](../marker-steps.md)).
- **Scenario reports** -- `SpecReport.For<T>()` accumulates a grid of what the system *did* (a
  `TableReport`, unjudged cells, capped at 200 rows) that reaches the console, the results file and
  the wire when the scenario fails.
- **Attachments** -- `SpecAttachment.Add/AddText/AddFile` hang a response body or a screenshot on the
  step, and `IFailureCapture` takes one automatically when a step fails
  ([Failure evidence](../marker-steps.md#attachments)).
- **The JSON results file** -- `BOBCAT_RESULTS=<dir>` or `bobcat run --json` writes every scenario's
  steps, cells, logs and reports as JSON, with a generated JSON Schema beside it. This is the
  machine-readable integration point ([The results file](../bobcat-tool.md#results-file)).
- **The query commands** -- `bobcat failing-tests`, `failing-spec`, `run-status` and the rest return
  JSON over a running `bobcat ui` ([Querying Runs from the Command Line](../agent-queries.md)).
- **The loop** -- `bobcat run --failed` reruns only what was red, by identity
  ([Rerunning what failed](../bobcat-tool.md#failed-repeat)).
- **`BOBCAT_SPEC_PREVIEW=1 ./MySpecs --list-tests`** answers "which step binds to what" without
  executing anything.
- **Scenario reports, step logs and attachments** carry what the system did, written out only for a
  failed specification -- see [Custom Telemetry on Failure](../failure-telemetry.md).

### 3a. The second worked example: agent assignments in Wolverine's leadership-election suite

Where a tracked session is a messaging story, a cluster's agent-assignment history is a distributed one:
which node held which agent, what the leader decided, and whether the assignment table matches what is
actually running. [Custom Telemetry on Failure](../failure-telemetry.md#worked-example-wolverine-s-leadership-election-compliance-suite)
walks through it, and `spikes/leadership-telemetry` is the code: a timeline report fed from the nodes'
own background threads, a placement report with the disagreements as red rows, step logs from the waits,
and the full transcript as an attachment.

### 4. Generalizing past Wolverine

The same argument for the event store (what was actually in the stream), for HTTP (the response
body on an unexpected status), and for the database. For the Critter Stack event stores,
WolverineFx.Bobcat's `EventStores.FetchStreamAsync` and `EventStores.AggregateStreamAsync<T>` are
the equivalents, and they are store-agnostic -- the same call answers for Marten, Polecat or Fisher.

### 5. Where to stop

Context is not free -- a test that dumps everything is as unreadable as one that dumps nothing, and
there is a hard limit worth naming: a single enormous string breaks the MTP wire outright. One real
suite dumped a 698MB tracked-session log into test output, faulted the worker, and turned every
unreported test indeterminate. "Write out more context" needs its boundary stated in the same
breath -- which is why a step log keeps 100 lines, a report 200 rows, and an attachment over its size
limit is kept as a reference, each with an "…and N more" rather than a silent cut.

## Open questions for the author

- The vehicles above now exist (step logs, reports, attachments, the results file). Is this page a
  guidance document over them, or is there still a Bobcat-side API missing -- for instance capturing
  the application's own `ILogger` output onto a step, which is not built?
