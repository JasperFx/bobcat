# Agent-assignment telemetry on failure: findings (bobcat#563)

**Result: the producer works, and it's the worked example of emitting custom telemetry through Bobcat
for a failing test.** A failed leadership specification now says three things the current suite
spreads over unstructured test output and an exception message: what the leader decided, in order, as
a grid (a scenario report); which agents the assignment table and the running nodes disagree about, as
red rows (a second report); and what the wait saw while it waited (step logs). The whole unfolded
history is attached as a file (a failure capture). A passing specification writes none of it.

Measured on Bobcat at `34c8909` against a reproduction of Wolverine's suite at `3eb4b6db9`
(`src/Testing/Wolverine.ComplianceTests/LeadershipElectionCompliance.cs`). Nothing in the Wolverine
checkout was changed. Porting it into Wolverine is the follow-up described at the end.

## 1. The instrumentation the suite has today

| What | Where | What it captures | How a failure surfaces it |
|---|---|---|---|
| `XUnitEventObserver` | `Wolverine.ComplianceTests/XUnitEventObserver.cs`, wired in `startHostAsync` | Every `IWolverineEvent` from each host's `runtime.Tracker`, which today means `ListenerState` only. It has a branch for `AgentAssignmentsChanged` (one line per agent), but **nothing in Wolverine constructs or publishes that event any more** (bobcat#670): the record is declared and never raised, so the branch is dead and a failing leadership test on Wolverine `main` writes no "Agent assignments determined" line at all | Written to `ITestOutputHelper` for every test, passing or not. No assignment history reaches it |
| `IWolverineObserver` | `runtime.Observer`, called by the node controller | **The assignment history**: `AssignmentsChanged(AssignmentGrid grid, AgentCommands commands)` on every evaluation, changed or not; `AgentStarted` / `AgentStopped` per node; `AssumedLeadership` / `LostLeadership`, `NodeStarted` / `NodeStopped`, `StaleNodes` | Not surfaced by the suite at all. This is the feed a producer subscribes to (section 4) |
| `OutputLoggerProvider` / `XUnitLogger` | `Wolverine.ComplianceTests/Logger.cs` | Every `ILogger` line from every node, prefixed with its category | Test output, unconditionally. **`IsEnabled` returns false for `LogLevel.Error`**, so a node's error-level log lines never appear. Exceptions of two types are dropped |
| `AssignmentWaiter` | `Wolverine/TestingExtensions.cs` (`WaitUntilAssignmentsChangeTo`) | On timeout only, `WritePersistedActualsAsync`: "According to the database…" (each node's `ActiveAgents` from `Storage.Nodes.LoadAllNodesAsync`), then "According to the runtimes" (each tracked runtime's running agents), and `(node … is not tracked by this waiter)` for a ghost row | The text is the **`TimeoutException`'s message**. It's the best diagnostic in the suite, but it's free text, it lists the two sides one after the other rather than per agent, and it has no history |
| `LeadershipWaiter`, `WaitForAnyLeadershipAsync` | same | Nothing | `TimeoutException("Did not assume the leadership in the time allowed")` / `"No node assumed leadership within …"`: no state at all |
| `expectExactlyOneCopyOfEachAsync` (around line 184) | the base class | Final placement across `_hosts` | Three Shouldly assertions (duplicated / not running anywhere / paused but running). **The first one to fail throws, so the others are never reported**, even though the comment explains that telling them apart is the point |
| `expectOneCopyWithARowNamingItsNodeAsync` (around line 274) | the base class | Runners of one agent, and its row's owner | `ShouldBe(1)` then `ShouldBe(nodeId)`: one message, first failure only, a node id rather than a number |
| `DoOnAgentHandler.Handled` (`AgentHandled`, line 853) | static `ConcurrentBag` | Which node number handled the command for each agent | `First(...)` throws "Sequence contains no matching element" when a forward never arrived; there is no record of which node was asked and where it forwarded |
| `FanOutHandler.Handled`, `BroadcastPingHandler.Handled` | static bags | Node numbers that handled the message | `ShouldContain` / `ShouldBe` over node numbers |

What that adds up to: **the history and the state at failure are never joined.** The history is in test
output, untimed, mixed with logs and present on every test; the state is in an exception message; and
the reader lines them up by hand. Several waits report only their first failure, and two report nothing.

## 2. What was built

`spikes/leadership-telemetry` reproduces the suite's shape with no database: an abstract base in a
library, one bodyless subclass per provider, the first node started in `InitializeAsync`, and Wolverine's
test names. The cluster publishes from each node's background loop, as Wolverine's tracker does. One
provider's leader **forgets the stop when it moves an agent between live nodes**, which is the
GH-3698/GH-4407 kind of bug: the rows follow the starts, so the table looks healthy while agents run twice.

The producer is `Compliance/Telemetry/`. Each piece uses the Bobcat vehicle that fits what it carries:

| Telemetry | Vehicle | Why that one |
|---|---|---|
| The leader's decisions over time: node events, and every agent an evaluation moved, with runs of no-change evaluations folded into one row | **Scenario report** `AgentAssignmentTimeline` (`IScenarioReport`, `OnFailure`, capped at 200 rows with "…and N more") | It belongs to the whole scenario, not to one step, and it's tabular |
| Where every agent is at the moment a wait gave up: the row's node against the nodes running it | **Scenario report** `AgentPlacementReport` (`TableReport`), with a **judged** cell per agent, so a duplicated or lost agent is a red row | Replaces `WritePersistedActualsAsync`'s text. One row per agent puts the two sides next to each other |
| What the wait saw while it waited ("0.10s: node 1 runs 12, node 2 runs 6") and what the step did ("node 1 stopped, taking the leadership with it") | **Step logs** (`SpecLog.Write`) on the step itself | A narrative of one step, a handful of lines, since it logs only when what it sees changes |
| The whole transcript, in `XUnitEventObserver`'s format with timestamps, including every no-change evaluation | **Attachment** from an `IFailureCapture` (`ClusterTranscriptCapture`), on the failed step | What you grep or diff against a green run. It's too long to read inline, and the timeline is what you read |
| Which nodes are wrong | **Gathered checks** in the step (`SpecAssert.Check` per node; duplicated / nowhere / wrong-row counts) | Every failure mode is reported together, not just the first one to throw |

The red specification, as the platform's test output shows it (the console and the JSON results file
carry the same):

```text
  add second node see balanced nodes FAILED
    ✓ Given a node starts
      Logs:
        node 2 started as f0766316-0c31-4835-83b5-ce89f2f2d982
    ✗ Then  the agents settle as 1=6, 2=6 (3000ms)
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

Read together: the leader moved six agents to node 2, the rows agree, and node 1 still runs all twelve,
so the stop was never sent. Wolverine's suite today gives a reader only a `TimeoutException` listing two
agent lists, with no history at all (section 1).

## 3. What it showed about Bobcat

1. **The ambient surfaces are silent from a background thread.** `SpecLog`, `SpecReport` and
   `ScenarioReports.Current` are `AsyncLocal`. An observer that Wolverine calls on a node's
   loop has no scenario, so writing a report or a log line from it **does nothing and says nothing**.
   The producer buffers into a `ConcurrentQueue` and turns it into cells on the test's side. This wasn't
   documented. The new docs page now states it.
2. **`TableReport` is not safe to append to from several threads**, and renderers read its cells. The
   pattern that works: implement `IScenarioReport`, and build a `TableReport` lazily from a snapshot the
   first time a renderer reads `Cells`, which is at the scenario's close. The cap and the row stamping
   stay `TableReport`'s. A concurrent or snapshot-on-read `TableReport` in core would save a producer
   this. That's a possible follow-up, not built here.
3. **A lazy report is built for passing scenarios too.** `ScenarioReportVisibility` filters on
   `r.Cells.Count > 0 && ShouldWrite(...)`, so `Cells` is read before visibility is checked. It's cheap
   here (one fold over a bounded queue), but checking `ShouldWrite` first would make a lazy report free
   on a green run. That's a one-line follow-up candidate, not changed here.
4. **xUnit v3 runs `IAsyncLifetime.InitializeAsync` before the `[BobcatSpec]` recording opens.** Wolverine
   starts its first host there, and a `SpecLog` line written there is lost (the spike's "node 1 started
   as …" never appears), as is a report attached there. Every step therefore attaches the timeline
   (`AttachToScenario`, idempotent), and the node's events survive because the telemetry buffers them.
   A failure *in* `InitializeAsync` isn't a step, so no `IFailureCapture` runs for it.
5. **A report is a usable per-scenario handle.** The failure capture finds this scenario's telemetry
   through `ScenarioReports.Current.Reports.OfType<AgentAssignmentTimeline>()`, because a capture runs
   with the test's execution context. No static "current test" registry was needed, which would have
   been wrong under parallel classes.
6. **An attachment over `SpecAttachment.MaxBytes` is kept as a reference, never truncated**, so an
   unbounded transcript would lose *all* of it on exactly the long run that needed it. The producer
   bounds its own buffer (newest 5,000 events, the rest counted and named in the transcript).
7. **A red row in a report doesn't change the verdict.** The placement grid's failed rows aren't
   counted as wrongs ("Rights: 2, Wrongs: 1"), which is right: the step's checks are the claim, and the
   report explains it.
8. **`SpecReport` had no user-facing documentation at all** before this change. It's now in
   `docs/failure-telemetry.md`, with this spike as the worked example.

## 4. Porting it to Wolverine

The producer belongs in **`Wolverine.ComplianceTests`**, next to `XUnitEventObserver`, not in
WolverineFx.Bobcat and not in Bobcat core. It's specific to the agent family and node storage this suite
drives, and no specification that uses WolverineFx.Bobcat's vocabulary has a cluster to observe.

| Spike | Wolverine |
|---|---|
| `IClusterEvent`, `ClusterNode.Tracker.Subscribe` | An **`IWolverineObserver` decorator** over `runtime.Observer`, installed between `Build()` and `StartAsync()`. **Not** `runtime.Tracker`: the tracker carries `ListenerState` only (bobcat#670) |
| `AgentAssignmentsChanged(LeaderNode, Evaluation, Nodes, Assignments, Commands)` | `IWolverineObserver.AssignmentsChanged(AssignmentGrid grid, AgentCommands commands)`, called on every evaluation, changed or not: `grid.AllAgents` (`OriginalNode` / `AssignedNode`, `.AssignedId`), `grid.Nodes`, `commands`. The evaluation number is the observer's own count. `AgentStarted` / `AgentStopped` add each node's actual starts and stops, which the spike doesn't have: that's what shows a missing stop ("evaluation 2: start on node 3" with no "stopped" on node 2) |
| `NodeStarted`, `NodeStopped`, `LeadershipAssumed` | `IWolverineObserver.NodeStarted` / `NodeStopped`, `AssumedLeadership` / `LostLeadership`, `StaleNodes` |
| `NodeStorage.LoadAllNodes()` | `runtime.Storage.Nodes.LoadAllNodesAsync(ct)`, so `AgentPlacementReport.Capture` becomes async |
| `ClusterNetwork.LiveNodes`, `RunningAgents()` | `_hosts`, `host.RunningAgents()` |
| `ClusterSteps.AgentsSettleAs("1=6, 2=6")` | `WaitUntilAssignmentsChangeTo(w => w.ExpectRunningAgents(…))`. The step gathers a check per host instead of throwing `WritePersistedActualsAsync`'s text |
| `ClusterSteps.EveryAgentRunsOnce()` | `expectExactlyOneCopyOfEachAsync` + `expectOneCopyWithARowNamingItsNodeAsync`, gathered instead of first-throw |

Two traps in installing the observer:

- **`NodeAgentController` captures `runtime.Observer` in its constructor, at host start.** A decorator
  set after `StartAsync()` (where `new XUnitEventObserver(host, _output)` is today) is never called.
  Install it between `Build()` and `StartAsync()`.
- **Forward every member explicitly, the default-bodied ones too** (`LostLeadership`, `AgentPaused`,
  `EventsAppended`, …). A decorator that leaves them to the interface's default bodies silently
  swallows the overrides of the observer it wraps (the persistence observer's).

What else the conversion takes:

- `[Fact]` becomes `[BobcatSpec]` on the base. Bodyless provider subclasses work since #600: each
  provider is its own feature (`[BobcatFeature("PostgreSQL leader election")]`).
- The private helpers become public methods on a step class the base holds. A `private` or
  `protected` step helper is BOBCAT039 (#535).
- `Wolverine.ComplianceTests` references Bobcat, Bobcat.Xunit and Bobcat.Generators (with the
  `Bobcat.Generated` interceptor opt-in), and each `*Tests.LeaderElection` host gets the
  `Bobcat.Xunit` session hook **and the generator**, as `spikes/leadership-telemetry/Providers` does:
  the inherited specs are bound in the subclass's compilation (#600). The library's `PrivateAssets`
  and `IsTestingPlatformApplication=false` are in `docs/spec-identities.md#base-in-a-library` (#671).
- `XUnitEventObserver` can stay for test output while the suite moves over, but it carries no
  assignment history (section 1); the transcript's per-agent wording comes from the observer feed.
- Unrelated to Bobcat, but found while reading: `XUnitLogger.IsEnabled` drops `LogLevel.Error`, which
  is probably the opposite of what a reader of a red leadership test wants.

That conversion is Wolverine work, and is recommended as a Wolverine issue that links this spike.
