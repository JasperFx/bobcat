# Agent Friendly Integration Tests

::: warning OUTLINE — NOT YET WRITTEN
This tutorial has no prose yet, and unlike the others it has **almost no existing material to draw
on** — the word "agent" appears five times across the entire documentation set, all incidental.
This is net-new writing.

The outline below records the argument it needs to make.
:::

## What this tutorial is for

A coding agent iterating on a failing integration test is working from whatever the failure said.
When that is `Expected True but was False`, the agent's next move is to add logging and run again —
and the loop costs a round trip that a better failure would have saved.

The thesis: **a test should write out what the system actually did, not only whether it matched.**
Integration tests have that information and usually throw it away.

## To cover

### 1. The argument, concretely

A before-and-after on one failure: the same broken behaviour reported two ways, and what each one
lets an agent do next. This is the section that has to land — the rest is mechanism.

### 2. The worked example: Wolverine's `TrackedSession`

This is the anchor case. A tracked session knows the whole messaging story — what was sent, what
was handled, in what order, and what threw — and a test that merely asserts an outcome discards all
of it.

- `Bobcat.Wolverine` surfaces tracked sessions directly:
  `InvokeMessageAndWaitAsync`, `SendMessageAndWaitAsync`, `ExecuteAndWaitAsync` and `TrackActivity`
  all hand back an `ITrackedSession` — see [Bobcat with Wolverine](../integrations/wolverine.md).
- The session's own diagnostics — `Status`, `AllExceptions()`, `AllRecordsInOrder()` — are the
  material. `AllRecordsInOrder()` in particular is a causal chain, which is exactly what an agent
  cannot reconstruct from an assertion message.
- Wolverine's own `AssertCondition` throws *with* the history attached, which is the pattern to
  generalize from.

### 3. The vehicles Bobcat already gives you

The point is not to print more to the console. It is to put the context somewhere structured:

- **`.WithRows(...)`** renders objects as a step's table — a message flow becomes a readable table
  rather than a log line.
- **Set verification reporting** already says which rows were missing, extra or wrong, rather than
  that the sets differed.
- **`run --json`** is the machine-readable report, and is the obvious thing for an agent to consume.
  Worth stating whether that is the recommended integration point.
- **`preview`** answers "which step bound to what" without executing anything — cheap for an agent
  to run when a step matched the wrong grammar.

### 4. Generalizing past Wolverine

The same argument for Marten (what was actually in the stream), for HTTP (the response body on an
unexpected status), and for the database. `Bobcat.Marten`'s `FetchStreamAsync` and
`AggregateStreamAsync` are the equivalents.

### 5. Where to stop

Context is not free — a test that dumps everything is as unreadable as one that dumps nothing, and
there is a hard limit worth naming: a single enormous string breaks the MTP wire outright. One real
suite dumped a 698MB tracked-session log into test output, faulted the worker, and turned every
unreported test indeterminate. "Write out more context" needs its boundary stated in the same
breath.

## Open questions for the author

- Is there a Bobcat-side API to build here, or is this purely a guidance document? An
  `IStepContext` seam for "attach this diagnostic to the current step" would be the obvious shape,
  and does not exist today.
- Does the JSON report already carry enough for an agent, or does this tutorial imply extending it?
