# Agent Friendly Integration Tests

::: warning OUTLINE -- NOT YET WRITTEN
This tutorial has no prose yet, and unlike the others it has **almost no existing material to draw
on** -- the word "agent" appears five times across the entire documentation set, all incidental.
This is net-new writing.

The outline below records the argument it needs to make.
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

- `WolverineFx.Bobcat` surfaces the tracked session directly: every act on a `WolverineSpec` runs
  inside one, and `LastAct.Session` hands it back -- see
  [Bobcat with Wolverine](../integrations/wolverine.md).
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
- **`JsonRenderer.RenderScenario`** is the machine-readable form of a recorded specification, and is
  the obvious thing for an agent to consume. Worth stating whether that is the recommended
  integration point.
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
body on an unexpected status), and for the database. `EventStores.FetchStreamAsync` and
`EventStores.AggregateStreamAsync<T>` are the equivalents, and they are store-agnostic -- the same
call answers for Marten, Polecat or Fisher.

### 5. Where to stop

Context is not free -- a test that dumps everything is as unreadable as one that dumps nothing, and
there is a hard limit worth naming: a single enormous string breaks the MTP wire outright. One real
suite dumped a 698MB tracked-session log into test output, faulted the worker, and turned every
unreported test indeterminate. "Write out more context" needs its boundary stated in the same
breath.

## Open questions for the author

- Is there a Bobcat-side API to build here, or is this purely a guidance document? A recorded step
  carries neither logs nor diagnostics today, so attaching structured context to a step is a gap
  this tutorial would have to fill or name.
- Does the JSON report already carry enough for an agent, or does this tutorial imply extending it?
