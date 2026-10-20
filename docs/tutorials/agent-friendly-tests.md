# Agent Friendly Integration Tests

<!-- JEREMY: first full draft, replacing the outline. The Wolverine code is quoted from the checkout
at 3eb4b6db9 (Reports.cs, WolverineScenario.cs, LeadershipElectionCompliance.cs, TestingExtensions.cs)
and the leadership example from spikes/leadership-telemetry. The "Message activity" rendering in Step 2
is illustrative: the columns and the red-cell rule are from Reports.cs, the rows are made up. -->

**The problem:** a coding agent iterating on a red integration test is working from whatever the
failure said. When that is `Expected True but was False`, or a `TimeoutException` with two lists of
URIs in it, the agent's next move is to add logging and run again. Every one of those round trips
costs the run's wall clock plus a slice of the agent's context window, and an integration suite's
wall clock is minutes, not milliseconds.

**What you will build:** a suite whose failures carry what the system actually did, in a shape an
agent reads once, and the loop around it: run, read the failure as JSON, fix, rerun only what was
red. This is the feedback loop Bobcat is built to optimize, and it is the same loop a person on
a bad afternoon wants.

This tutorial is written from two real suites in the Wolverine repository: the asynchronous
messaging specifications that `WolverineFx.Bobcat` wraps around Wolverine's `TrackedSession`, and
the leadership-election compliance suite, which has been reworked as
[`spikes/leadership-telemetry`](https://github.com/JasperFx/bobcat/tree/main/spikes/leadership-telemetry)
in this repository. Both are about distributed behaviour that an assertion message cannot explain.

## The thesis

**A test should write out what the system did, not only whether it matched.** Integration tests
have that information and usually throw it away. The system under test knows which messages it
sent, what it decided, which node did what, what ended up in the stream. A test that asserts the
outcome and nothing else discards all of it at the moment it would have been most useful.

For a person, that is an irritation: they attach a debugger, or add a `Console.WriteLine` and run
again. An agent cannot attach a debugger. Its only move is the second one, and the loop it runs is:

1. Run the suite, or the one test. Wait.
2. Read the failure.
3. Guess at what the system did.
4. Add logging to test the guess. Run again. Wait.
5. Repeat until the guess is right.

Everything after step 2 is the cost of a failure that said too little. The cheapest round trip is
the one the agent does not take, so the job is to make step 2 answer the question step 3 asks.

What an agent needs from a failure, concretely:

| It needs the failure to be | Because |
|---|---|
| **complete**: every disagreement, not the first one to throw | a second wrong hidden behind a first is a second round trip |
| **structured**: expected beside actual, rows in a grid, not a sentence it has to parse | an agent reads text, and parsing prose is where it guesses wrong |
| **in the system's terms**: what was sent, what was appended, which node ran what | that is the question it is about to ask |
| **bounded**: a few hundred lines at most | a 50,000-line dump costs more context than it saves, and may not fit at all |
| **machine-readable**: JSON with a stable schema, reachable from a shell | an agent that can `jq` a results file needs no screen-scraping and no tool schema in its context |
| **rerunnable**: one command that runs only the red specification | the fix is checked in one short run, not another full one |

The rest of this page is those six properties, in the order you would add them to a suite.

## Step 1: gather every disagreement before anything throws

An assertion library throws, and a throw ends the test method, so an ordinary test can only ever
report its first disagreement. Wolverine's leadership compliance suite has a helper whose own
comment explains why that is a problem:

```csharp
// src/Testing/Wolverine.ComplianceTests/LeadershipElectionCompliance.cs
var final = runningAgentsAcrossTheCluster();

// Two distinct failure modes, and the difference matters: a MISSING agent means a stop was issued
// without a start reaching anyone, a DUPLICATED one means a start was issued without a stop.
final.Where(x => expected.Contains(x)).GroupBy(x => x).Where(g => g.Count() > 1)
    .Select(g => g.Key).ShouldBeEmpty("These agents are running on more than one node");

expected.Where(uri => !final.Contains(uri)).ShouldBeEmpty("These agents are not running anywhere");

stopped.Where(uri => final.Contains(uri)).ShouldBeEmpty("These agents were paused but are running");
```

The comment is right that telling the two apart is the point, and the code cannot do it: the first
`ShouldBeEmpty` to fail throws, and the other two never run. A reader gets one of three facts and
has to rerun to learn whether the others hold.

`SpecAssert.Check` records a comparison on the step in progress and carries on
([Checks that gather](../marker-steps.md#checks-that-gather-instead-of-throwing)). The same three
claims, as the spike writes them:

```csharp
var wrong = placement().Where(x => !healthy(x)).ToArray();
SpecAssert.Check("duplicated agents", wrong.Count(x => x.Runners.Length > 1), 0);
SpecAssert.Check("agents running nowhere", wrong.Count(x => x.Runners.Length == 0), 0);
SpecAssert.Check("rows naming the wrong node", wrong.Count(x => x.Runners.Length == 1), 0);
```

Three cells on the step, each named, each with its expected and actual, all three reported however
many fail. The runner is still told at the end: the gathered wrongs become the test's verdict once,
when the adapter's after-test hook throws them, so a red specification is never a green test.

Every `Then…` in the `WolverineFx.Bobcat` vocabulary already reports this way. A specification that
asserts three events and gets one wrong shows the one that disagreed and the two that held.

## Step 2: say what the system did, from the vocabulary

Wolverine's `TrackedSession` is the worked example of a system that knows the whole story. It
records every envelope: what was sent, where, what was handled, in what order, what was retried,
what threw, what went to the error queue. `AllRecordsInOrder()` is a causal chain, which is exactly
the thing an agent cannot reconstruct from an assertion message.

Wolverine's own tracking already understands this. When a tracked session's condition fails, it
throws **with the history attached**:

```csharp
// src/Wolverine/Tracking/TrackedSession.cs
public void AssertCondition(string message, Func<bool> condition)
{
    if (condition()) return;

    var description = BuildActivityMessage(message);   // "Activity detected:" plus a grid of every envelope record
    throw new Exception(description);
}
```

That is the pattern to generalize: the failure carries the activity. `WolverineFx.Bobcat` takes it
one step further by making the activity a **scenario report** that every act records, so no spec
author has to remember to ask for it:

```csharp
// src/Bobcat/Wolverine.Bobcat/Reports.cs, abbreviated
public sealed class MessageActivityReport : TableReport
{
    public override string Title => "Message activity";

    public void Add(ITrackedSession session)
    {
        foreach (var record in session.AllRecordsInOrder())
        {
            var failed = record.MessageEventType is MessageEventType.MessageFailed or MessageEventType.MovedToErrorQueue
                || record.Exception is not null;

            Row(new List<CellResult>
            {
                new("at (ms)", ResultStatus.ok, record.SessionTime.ToString()),
                failed
                    ? new CellResult("event", ResultStatus.failed) { Actual = record.MessageEventType.ToString(), Note = record.Exception?.Message }
                    : new CellResult("event", ResultStatus.ok, record.MessageEventType.ToString()),
                new("message", ResultStatus.ok, record.Message?.GetType().Name ?? ""),
                new("destination", ResultStatus.ok, record.Envelope?.Destination?.ToString() ?? ""),
                new("attempt", ResultStatus.ok, record.AttemptNumber.ToString()),
                new("service", ResultStatus.ok, record.ServiceName ?? "")
            });
        }
    }
}
```

and at the close of every act, `WhenReceived`, `WhenPublished`, `WhenPosted` and the rest:

```csharp
// src/Bobcat/Wolverine.Bobcat/WolverineScenario.cs
if (SpecReport.IsRecording)
{
    if (session is not null) SpecReport.For<MessageActivityReport>().Add(session);
    if (appended.Count > 0) SpecReport.For<AppendedEventsReport>().Add(appended);
}
```

So a specification written as nothing more than

```csharp
[BobcatSpec(typeof(ConfirmAppointment))]
public async Task confirming_sends_the_email()
{
    await GivenEvents<Appointment>(appointmentId, new AppointmentRequested(appointmentId, clientId));
    await WhenReceived(new ConfirmAppointment(appointmentId));
    ThenEvents(new AppointmentConfirmed(appointmentId));
    ThenMessageSent(new SendConfirmationEmail(clientId));
}
```

fails like this when the email handler throws on its first attempt and the message is moved to the
error queue:

```text
  confirming sends the email FAILED
    ✓ Given events: AppointmentRequested
    ✓ When ConfirmAppointment is received
    ✓ Then events: AppointmentConfirmed
    ✗ Then message sent: SendConfirmationEmail
        ✗ message: expected 'SendConfirmationEmail', got no matching message

Message activity
at (ms) | event              | message               | destination               | attempt | service
------- | ------------------ | --------------------- | ------------------------- | ------- | --------
0       | Received           | ConfirmAppointment    | local://confirmappointment | 1       | Scheduling
3       | ExecutionStarted   | ConfirmAppointment    | local://confirmappointment | 1       | Scheduling
19      | Sent               | SendConfirmationEmail | rabbitmq://queue/emails   | 1       | Scheduling
21      | ExecutionFinished  | ConfirmAppointment    | local://confirmappointment | 1       | Scheduling
40      | ExecutionStarted   | SendConfirmationEmail | rabbitmq://queue/emails   | 1       | Notifications
44      | MessageFailed      | SendConfirmationEmail | rabbitmq://queue/emails   | 1       | Notifications  <-- FAILED  SmtpException: 550 relay denied
45      | MovedToErrorQueue  | SendConfirmationEmail | rabbitmq://queue/emails   | 1       | Notifications  <-- FAILED

Events appended
stream        | version | event                | data
------------- | ------- | -------------------- | -------------------------
appointmentId | 2       | AppointmentConfirmed | AppointmentId: appointmentId
```

Read it once and the diagnosis is on the page: the command was handled, the event was appended,
the email *was* sent, the Notifications service failed it with an SMTP error and dead-lettered it.
The fix is in the email handler or the relay, not in `ConfirmAppointment`. Without the report, the
agent has `got no matching message` and is about to go looking in the wrong handler.

Three things about this worth copying into a vocabulary of your own:

- **The report is recorded by the shared helper, not by each test.** An agent writing its tenth
  specification does not have to know the report exists to get it. Put the telemetry where the
  act is, once.
- **A passing specification pays nothing.** `ReportVisibility.OnFailure` is the default, so the
  grid is built but never written for a green scenario, and a green suite's output stays short.
  `bobcat run --verbose` writes every report when you want to see one that passed.
- **The grid is still an account, not a claim.** The red cells on the `MessageFailed` row explain
  the failure; the verdict comes from the `ThenMessageSent` check. A report cannot make a
  specification fail, so a noisy report cannot make a green one red.

The whole mechanism, and the other two vehicles beside it, are on
[Custom Telemetry on Failure](../failure-telemetry.md). For an event-sourced application the
`AppendedEventsReport` is the same idea for the stream: every event the act appended, beside a
`ThenEvents` that may only have asked about some of them.

## Step 3: a history from the system's own threads

A tracked session hands the test its records on the test's own thread. A cluster does not. Wolverine's
leadership-election suite fails in ways only the assignment history explains: which node held which
agent, what the leader decided at each evaluation, and whether the assignment table in the database
matches what is actually running. The suite's diagnostics today are a `TimeoutException` whose
message lists the two sides one after the other:

```text
According to the database...
Node 1 is running:
fake://eight/
fake://eleven/
…
According to the runtimes
Node 1 is running:
fake://eight/
…
```

There is no history, the reader lines the two lists up by hand, and the event observer that *does*
see the history writes it to test output for every test, passing or not, untimed, mixed in with the
logs. An agent handed that failure is at step 3 of the loop above, guessing.

The spike reworks the same suite so that one failed specification says four things:

```text
  add second node see balanced nodes FAILED
    ✓ Given a node starts
      Logs:
        node 2 started as f0766316-0c31-4835-83b5-ce89f2f2d982
    ✗ Then the agents settle as 1=6, 2=6 (3000ms)
        ✗ node 1: expected '6', got '12'
        ✓ node 2: 6
      📎 cluster-transcript.txt (text/plain, 39.4 KB)
      Logs:
        0.00s: node 1 runs 12, node 2 runs 0
        0.10s: node 1 runs 12, node 2 runs 6
        gave up after 3s

Agent assignments over time
at     | leader | evaluation | agent          | change
------ | ------ | ---------- | -------------- | --------------------------------
0.000s |        |            |                | node 1 started
0.000s | 1      |            |                | node 1 assumed leadership
0.000s | 1      | 1          | fake://one/    | assigned to node 1
…        (the other eleven agents, assigned to node 1)
0.000s |        |            |                | node 2 started
0.051s | 1      | 2          | fake://seven/  | reassigned from node 1 to node 2
…        (eight to twelve, the same)
0.102s |        |            |                | 58 evaluation(s), no changes

Agent placement: assignment rows against running agents
agent          | row names | running on | verdict
-------------- | --------- | ---------- | ------------------------------------------------------------------------
fake://one/    | 1         | 1          | one copy, row agrees
…
fake://seven/  | 2         | 1, 2       | expected 'running once, on node 2', got 'running 2 times, on nodes 1, 2'  <-- FAILED
…
```

Read together: the leader moved six agents to node 2 at evaluation 2, the assignment rows agree, and
node 1 is still running all twelve. The stop was never sent. That is the whole diagnosis, from one
read, and it is the GH-3698/GH-4407 class of bug that took real people real time to find.

Each piece of that output is a different Bobcat vehicle, chosen for the shape of what it carries:

| What it says | Vehicle | Why that one |
|---|---|---|
| What the leader decided, over time | a scenario report, `AgentAssignmentTimeline` | it belongs to the whole scenario, not one step, and it is a table |
| Where every agent is at the moment the wait gave up, with disagreements as red rows | a second scenario report, `AgentPlacementReport` | one row per agent puts the two sides next to each other, which the exception's two lists never did |
| What the wait saw while it waited | step logs, `SpecLog.Write`, written only when what it sees changes | a narrative of one step, a handful of lines |
| Which nodes are wrong | gathered checks, one per node | every failure mode reported together (Step 1) |
| The whole transcript, every evaluation | an attachment from an `IFailureCapture` | too long to read inline; what an agent greps, or diffs against a green run's |

The wait itself is the step. It narrates what it sees as it polls, and when it gives up it captures
the placement grid and records one cell per node instead of throwing a string:

```csharp
[Then("the agents settle as {placement}")]
public async Task AgentsSettleAs(string placement)
{
    Telemetry.AttachToScenario();
    var expected = parse(placement);                       // "1=6, 2=6" → { 1: 6, 2: 6 }

    if (await pollAsync(() => expected.All(x => counts()[x.Key] == x.Value),
            () => string.Join(", ", counts().Select(x => $"node {x.Key} runs {x.Value}"))))
    {
        return;
    }

    AgentPlacementReport.Capture(Network);

    var final = counts();
    foreach (var (number, count) in expected)
    {
        SpecAssert.Check($"node {number}", final[number], count);
    }
}

private async Task<bool> pollAsync(Func<bool> done, Func<string> describe)
{
    string? last = null;
    using var timeout = new CancellationTokenSource(Timeout);
    while (true)
    {
        var now = describe();
        if (now != last)                                    // a line when the picture changes, not per poll
        {
            SpecLog.Write($"{elapsed():0.00}s: {now}");
            last = now;
        }

        if (done()) return true;
        if (timeout.IsCancellationRequested) break;
        await Task.Delay(PollingInterval);
    }

    SpecLog.Write($"gave up after {Timeout.TotalSeconds:0.#}s");
    return false;
}
```

**The one rule that catches every producer of this kind of telemetry:** `SpecLog`, `SpecReport` and
`SpecAttachment` find the scenario through the async context they are called from. An observer the
runtime calls from a node's own background loop has no scenario, so a log line or report row written
there is dropped without a sound. The timeline report therefore **buffers** what the nodes publish,
into a bounded `ConcurrentQueue`, and builds its grid on the test's side the first time a renderer
asks for it, which is at the scenario's close. The transcript attachment finds that same buffer
through the report the scenario carries, because a failure capture *does* run in the test's context.
[Custom Telemetry on Failure](../failure-telemetry.md#where-the-telemetry-comes-from-matters) has
the code for both, and the port to Wolverine's real observer is laid out in the spike's
[`findings.md`](https://github.com/JasperFx/bobcat/blob/main/spikes/leadership-telemetry/findings.md).

## Step 4: the same argument past Wolverine

Nothing above is specific to messaging or to clusters. The question is always "what did the system
have, when the check disagreed?", and the answer lives somewhere the test can reach:

| The system | What it knows | Where to put it |
|---|---|---|
| An event store | what is actually in the stream | an `AppendedEventsReport`, or a report of your own from `EventStores.FetchStreamAsync` / `AggregateStreamAsync<T>` in `WolverineFx.Bobcat`, which answer the same for Marten, Polecat and Fisher |
| An HTTP endpoint | the response body on an unexpected status | `SpecLog.Write($"POST /orders -> {status}")` on the step, and the body as a `SpecAttachment` |
| A browser | the page it was looking at | a screenshot from an `IFailureCapture`, taken for every failed step without any spec asking ([Failure evidence](../marker-steps.md#attachments)) |
| A database | the rows the query returned | a `TableReport` with one row per row |
| A set comparison | which rows were missing, extra or wrong | already done: a set verification reports missing, extra and out-of-order rows, never "the sets differed" ([Data Intensive Specifications](data-intensive-specifications.md)) |

The vehicles are the same four every time:

| You have | Put it in | Renders as |
|---|---|---|
| a claim | `SpecAssert.Check` | an expected/actual cell on the step, which is what fails it |
| a short narrative of one step | `SpecLog.Write` | lines under that step |
| a table that describes the whole scenario | `SpecReport.For<T>()` | a titled grid after the steps |
| something too long to read inline | `SpecAttachment`, or an `IFailureCapture` | a file linked from the step |

## Step 5: the loop around it

A failure that says enough is half of the loop. The other half is that the agent can run, read and
rerun from a shell with nothing else set up.

### Read the failure as JSON

```bash
bobcat run --json | jq '.scenarios[] | select(.succeeded | not)'
```

`--json` prints the [results file](../bobcat-tool.md#results-file) on standard output and everything
else on standard error. The file holds every scenario's identity and verdict, every step with its
status, timing, cells (`expected`, `actual`, `note`, or `value` for an unjudged input), its `logs` and
`attachments`, and the scenario's `reports` as grids of the same cells. It carries a `$schema`, and
the schema is generated from the records the file is written from, so an agent can read the shape
instead of guessing at it:

```bash
bobcat results-schema                      # print the schema
bobcat run --results artifacts/bobcat      # the file, its schema and the attachments as files, beside it
BOBCAT_RESULTS=artifacts/bobcat dotnet test   # the same file from plain dotnet test, any runner
```

An attachment in a results directory is a real file at a path the step names, so the transcript
from Step 3 is one `cat` away.

### Ask a running console instead

With a [`bobcat ui`](../bobcat-ui.md) running, the same facts are a shell command each, as JSON on
standard output ([Querying Runs from the Command Line](../agent-queries.md)):

```bash
bobcat ui --no-browser &          # once, on the box
bobcat failing-tests              # every failed scenario in the latest run, with its failing steps
bobcat failing-spec               # everything about the first failure: steps, cells, logs, reports, exception
bobcat await-run --timeout 900    # block until the run in flight finishes, then print its summary
```

These are deliberately CLI commands rather than MCP tools: an agent asks with a shell command it
already knows how to run, reads JSON back, and carries no tool schema in its context for the
privilege.

### Rerun only what was red

```bash
bobcat run                        # 3 of 40 red
bobcat run --failed               # those 3, by identity
bobcat run --failed               # after the fix: whatever is still red
```

`--failed` runs the specifications whose scenario did not succeed last time, by their
`{Feature}/{Scenario}` identity ([Rerunning what failed](../bobcat-tool.md#failed-repeat)). Two
properties matter for an agent in particular. A specification renamed or deleted since is **refused
by name**, and nothing runs, so a rerun can never quietly drop a test and read as green. And a last
run whose host died before writing results is refused too, rather than read as "nothing failed".

`bobcat watch` reruns on every change to the spec project or anything it references, and narrows
with the same `--feature`, `--scenario` and `--slice` flags as `run`. For a suite whose cost is the
process rather than the fixtures, a [resident runner](../resident-runner.md) keeps a test host warm
between commands ([Warm Runs](../warm-projected-runs.md)); measured on this repository's own
samples, the second and third run requests into one live host took 11ms and 4ms where the first
took 72ms.

### Fail at compile time where you can

The cheapest failure of all is the one the build reports before anything runs. Bobcat's source
generator checks what it can at compile time and names the fix in the message
([Diagnostics](../diagnostics.md)): a table column that names no member of its type (BOBCAT032),
a step helper the interceptor cannot reach (BOBCAT039), a project that never opted into the
interceptors so its specifications would silently record nothing (BOBCAT037), two specifications
with one identity (BOBCAT036). An agent reads a diagnostic id, and the
[ai-skills](https://github.com/JasperFx/ai-skills) entry for Bobcat tells it what each one means
and the move that fixes it.

And a specification can be read without running it: `bobcat preview` renders the steps a
specification declares, so "which step binds to what" is answered before the suite spends a
minute finding out.

## Where to stop

Context is not free, and "write out more" needs its boundary stated in the same breath. A test that
dumps everything is as unreadable as one that dumps nothing, and there is a harder limit than
readability: one real suite dumped a 698MB tracked-session log into its test output, faulted the
worker process, and turned every unreported test in the run indeterminate. A run that read as a
crash because something tried to say too much.

So every vehicle above is capped, and every cap says so rather than cutting silently:

| Vehicle | Cap | What you see past it |
|---|---|---|
| a step log | 100 lines per step | `…and N more lines` |
| a scenario report | 200 rows (override `MaxRows`) | `…and N more` |
| an attachment | 10 MB, and 20 per scenario | a reference marked `omitted`, never a truncated file; `…and N more attachments not kept` |
| Wolverine's own activity grid | the last 500 envelope records | `(showing last 500 of N envelope records …)` |

Two habits keep a producer under them. **Log on change, not on every poll**: the wait in Step 3
writes a line when the picture it sees changes, so a three-second wait is three lines, not sixty.
**Bound your own buffer** before Bobcat has to: the timeline keeps the newest 5,000 events and
counts the rest, because an attachment over the cap is kept only as a reference, and an unbounded
transcript would lose all of it on exactly the long run that needed it.

## Where to go next

- The four vehicles, in reference form, with the async-context rule and the lazy report pattern:
  [Custom Telemetry on Failure](../failure-telemetry.md)
- Step logs and attachments on the step: [Writing Specifications](../marker-steps.md#step-logs)
- The results file and its schema, `--failed` and `--repeat`: [The `bobcat` Tool](../bobcat-tool.md)
- The query commands over a running console: [Querying Runs from the Command Line](../agent-queries.md)
- The Critter Stack vocabulary that records the message activity and appended events for you:
  [Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat) in the Wolverine docs
- Getting the same failures out of a CI job: [Integrating Bobcat with CI](continuous-integration.md)
