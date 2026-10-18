# Custom Telemetry on Failure

A red integration test usually needs more than its assertion message. The system under test knows the
story: what it sent, what it decided, which node did what. A test that only asserts the outcome throws
all of that away. Bobcat gives you four places to put that story so it's written out **when a
specification fails** and costs a passing one nothing:

| You have | Put it in | Renders as |
|---|---|---|
| A short narrative of one step: what a wait saw, what an act did | a step log, `SpecLog.Write` | lines under that step |
| A table that describes the whole scenario: a history, a state | a **scenario report**, `SpecReport` | a titled grid after the steps |
| Something too long to read inline: a transcript, a dump, a screenshot | an attachment, `SpecAttachment` or an `IFailureCapture` | a file, linked from the step |
| A claim | a check, `SpecAssert.Check` | a cell on the step, which is what fails it |

Step logs and attachments are covered in [Writing Specifications](marker-steps.md#saying-what-the-system-did-speclog-write-bobcat-547)
and [Failure evidence](marker-steps.md#attachments). This page covers scenario reports and the one rule
that catches every producer of telemetry: **where the telemetry is produced from.** It ends with a
worked example.

## Scenario reports -- `SpecReport`

A report is a titled table of cells that describes what the system did during the scenario. Derive from
`TableReport`, give it a title, and append rows:

```csharp
using Bobcat;
using Bobcat.Engine;

public sealed class EnvelopeReport : TableReport
{
    public override string Title => "Envelopes";

    public void Add(Envelope e) => Row(("message", e.MessageType), ("destination", e.Destination), ("status", e.Status));
}
```

From any step helper, or any code that runs inside the test, ask for this scenario's instance and add
to it:

```csharp
SpecReport.For<EnvelopeReport>().Add(envelope);
```

- **One instance per report type per scenario.** `SpecReport.For<T>()` creates it on first ask, so
  several helpers that agree on nothing but the type append to one table. `SpecReport.Attach(report)`
  puts an instance you built yourself on the scenario, replacing any earlier one of the same type.
- **Written out only for a failed scenario.** That's `ReportVisibility.OnFailure`, the default.
  Override `Visibility` to return `Always` for a report whose point is the passing case. A verbose run
  (`bobcat run --json --verbose`, `BOBCAT_VERBOSE_REPORTS=1`) writes every report. An empty report is
  never written.
- **Capped.** 200 rows by default (override `MaxRows`), then "…and N more", never a silent
  truncation.
- **Rows are unjudged**, so a report can't claim it checked anything. To make one row red, append
  a judged `CellResult`. The `Row(IReadOnlyList<CellResult>)` overload takes cells you built:

  ```csharp
  Row([
      new CellResult("agent", ResultStatus.ok, agent.ToString()),
      new CellResult("verdict", ResultStatus.failed) { Expected = "running once, on node 2", Actual = "running on nodes 1, 2" }
  ]);
  ```

  A red row explains a failure but doesn't cause one: the scenario's verdict comes from its checks.
- **Everywhere the run goes**: after the steps in the console, in the runner's own test output (as a
  plain-text grid with `<-- FAILED` on a red row), under `reports` in the
  [JSON results file](bobcat-tool.md), and on the monitor wire for `bobcat ui`.
- **Outside a specification it does nothing.** `SpecReport.For<T>()` hands back a throwaway instance,
  so a helper shared with ordinary tests still works.

## Where the telemetry comes from matters

`SpecReport`, `SpecLog` and `SpecAttachment` find the scenario through the async context they're called
from. That's what lets a helper in another package call them without being handed anything, and it's
what keeps parallel tests out of each other's reports. It also means:

**Called from a thread the system started, they do nothing.** An `IObserver` that a runtime calls from
its own background loop, a message handler running in the host, a timer callback: none of them is in the
test's async context, so a log line or a report row written there is dropped without a sound.

So a producer fed from the system's own threads buffers what it hears and turns it into a report on the
test's side. A report doesn't have to derive from `TableReport`. Implement `IScenarioReport` and build
the table from a snapshot the first time a renderer reads it, which happens at the scenario's close:

```csharp
public sealed class AssignmentTimeline(AssignmentTelemetry telemetry) : IScenarioReport
{
    private readonly Lazy<Grid> _grid = new(() => Grid.From(telemetry.Snapshot()));

    public string Title => "Agent assignments over time";
    public string? ShortTitle => null;
    public ReportVisibility Visibility => ReportVisibility.OnFailure;
    public IReadOnlyList<string> Columns => _grid.Value.Columns;
    public IReadOnlyList<CellResult> Cells => _grid.Value.Cells;
    public int SuppressedRows => _grid.Value.SuppressedRows;

    // Grid is a TableReport, so the cap and the row numbering are still TableReport's.
}
```

The observer only enqueues (into a `ConcurrentQueue`, which is also safe from several threads where a
`TableReport` is not), and attaching the report is done from a step: `SpecReport.Attach(timeline)`.
Bound the buffer yourself. An attachment over `SpecAttachment.MaxBytes` is kept only as a reference
marked omitted, so an unbounded transcript loses *all* of its contents on exactly the long run that
needed it.

**xUnit v3 runs `IAsyncLifetime.InitializeAsync` before the scenario opens**, so the same goes for it.
If your fixture starts the system there, attach the report from the steps, not from `InitializeAsync`.
The events from startup aren't lost as long as the producer is already buffering them.

An `IFailureCapture` *does* run in the test's async context, so it can find the scenario's own
telemetry through the report it carries, with no static registry of the current test:

```csharp
var timeline = ScenarioReports.Current?.Reports.OfType<AssignmentTimeline>().FirstOrDefault();
```

## Worked example: Wolverine's leadership-election compliance suite

Wolverine's leadership-election tests fail in ways only the cluster's assignment history explains:
which node held which agent, what the leader decided, and whether the assignment table matches what's
actually running. Today the suite records none of that history. The state at the moment of failure is
in a `TimeoutException`'s message.

[`spikes/leadership-telemetry`](https://github.com/JasperFx/bobcat/tree/main/spikes/leadership-telemetry)
reproduces the suite with no database and moves that telemetry into Bobcat's model:

- **a timeline report**: every node event and every agent the leader moved, with runs of evaluations
  that changed nothing folded into one row. It's fed from each node's background loop, so it's the
  buffered, lazily-built report above. In Wolverine the feed is an `IWolverineObserver` decorator over
  `runtime.Observer` (`AssignmentsChanged`, `AgentStarted` / `AgentStopped`), installed between `Build()`
  and `StartAsync()` and forwarding every member, not `runtime.Tracker`, which carries no assignment events.
- **a placement report**, built inside the step that gave up: for every agent, the node its row names
  against the nodes running it, with the disagreements as red rows.
- **step logs** from the waits: one line each time what they see changes.
- **the full transcript as an attachment**, from an `IFailureCapture` on the failed step.

Its [`findings.md`](https://github.com/JasperFx/bobcat/blob/main/spikes/leadership-telemetry/findings.md)
has the analysis of the instrumentation the suite has now, the red output side by side, and the port to
Wolverine.
