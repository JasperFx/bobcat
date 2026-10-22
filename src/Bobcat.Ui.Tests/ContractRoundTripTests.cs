using System.Text.Json;
using Shouldly;
using Client = Bobcat.Monitoring;
using Wire = Bobcat.Ui.Contracts;
using MonitorHost = Bobcat.Ui;

namespace Bobcat.Ui.Tests;

/// <summary>
/// The publisher records in Bobcat.Monitoring and the ingestion contracts in
/// Bobcat.Ui.Contracts are deliberately NOT a shared assembly — the wire shape is the
/// contract. These round-trips are what keep the two sides honest: every client event must
/// deserialize into the matching server contract with nothing lost.
/// </summary>
public class ContractRoundTripTests
{
    private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    private static Wire.MonitorEvent roundTrip(Client.MonitorEvent @event)
    {
        var json = JsonSerializer.Serialize(@event, options);
        var wire = JsonSerializer.Deserialize<Wire.MonitorEvent>(json, options);
        wire.ShouldNotBeNull();
        return wire;
    }

    private static readonly Guid runId = Guid.NewGuid();

    [Fact]
    public void run_started_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.RunStarted(runId, "Suite", "/repo", "main", "in-process", at, 42, "epic/gate"))
            .ShouldBeOfType<Wire.RunStarted>();

        wire.RunId.ShouldBe(runId);
        wire.Suite.ShouldBe("Suite");
        wire.Repository.ShouldBe("/repo");
        wire.Branch.ShouldBe("main");
        wire.Mode.ShouldBe("in-process");
        wire.StartedAt.ShouldBe(at);
        wire.TotalScenarios.ShouldBe(42);
        wire.Tag.ShouldBe("epic/gate");
    }

    [Fact]
    public void run_started_without_a_tag_still_round_trips()
    {
        // An old publisher's JSON has no tag member at all — additive means additive.
        roundTrip(new Client.RunStarted(runId, "Suite", "/repo", null, "in-process", DateTimeOffset.UtcNow, null))
            .ShouldBeOfType<Wire.RunStarted>().Tag.ShouldBeNull();
    }

    [Fact]
    public void the_session_and_command_that_started_a_run_reach_the_run_summary()
    {
        // bobcat#389 stamps CLAUDE_CODE_SESSION_ID as "session", bobcat#392 the resident runner's
        // command id as "command" — both trailing optionals on run_started, both released in 0.29.0.
        // A real round trip now, where it used to be hand-spelled JSON (stoat#80).
        var started = roundTrip(new Client.RunStarted(runId, "Suite", "/repo", "main", "in-process",
                DateTimeOffset.UtcNow, 3, "train/build", Session: "s-abc", Command: "6f1c7a64-1b1e-4c55-9d1a-2f5b8e0a9c11"))
            .ShouldBeOfType<Wire.RunStarted>();
        started.Session.ShouldBe("s-abc");
        started.Command.ShouldBe("6f1c7a64-1b1e-4c55-9d1a-2f5b8e0a9c11");

        var dataPath = Path.Combine(Path.GetTempPath(), $"stoat-session-{Guid.NewGuid():N}");
        try
        {
            using var registry = new MonitorHost.Runs.MonitorRunRegistry(dataPath);
            registry.Record([started]);
            var summary = MonitorHost.RunEndpoints.All(registry).Single();
            summary.Session.ShouldBe("s-abc");
            summary.Command.ShouldBe("6f1c7a64-1b1e-4c55-9d1a-2f5b8e0a9c11");
        }
        finally
        {
            try { Directory.Delete(dataPath, recursive: true); } catch { }
        }
    }

    [Fact]
    public void a_run_with_no_session_or_command_carries_neither()
    {
        // Every run started any other way: an older publisher, or a run nobody commanded.
        var started = roundTrip(new Client.RunStarted(runId, "Suite", "/repo", null, "in-process", DateTimeOffset.UtcNow, null))
            .ShouldBeOfType<Wire.RunStarted>();
        started.Session.ShouldBeNull();
        started.Command.ShouldBeNull();
    }

    [Fact]
    public void run_heartbeat_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.RunHeartbeat(runId, at)).ShouldBeOfType<Wire.RunHeartbeat>();
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void run_finished_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.RunFinished(runId, 1, 10, 2, 3, 1, at))
            .ShouldBeOfType<Wire.RunFinished>();

        wire.ExitCode.ShouldBe(1);
        wire.Passed.ShouldBe(10);
        wire.Failed.ShouldBe(2);
        wire.PassedOnRetry.ShouldBe(3);
        wire.Indeterminate.ShouldBe(1);
        wire.FinishedAt.ShouldBe(at);
        // Nothing dropped travels as null, never zero (bobcat#767).
        wire.DroppedEvents.ShouldBeNull();

        roundTrip(new Client.RunFinished(runId, 1, 10, 2, 3, 1, at, DroppedEvents: 37))
            .ShouldBeOfType<Wire.RunFinished>().DroppedEvents.ShouldBe(37);
    }

    [Fact]
    public void scenario_started_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.ScenarioStarted(runId, "F/s", "F", "s", 2, at))
            .ShouldBeOfType<Wire.ScenarioStarted>();

        wire.Uid.ShouldBe("F/s");
        wire.Feature.ShouldBe("F");
        wire.Scenario.ShouldBe("s");
        wire.Attempt.ShouldBe(2);
    }

    [Fact]
    public void the_declared_narrative_round_trips_with_the_announcement()
    {
        // Issue #304. The narrative is the one thing a marker-comment spec knows that nothing
        // else does, and it crosses the wire on ScenarioStarted rather than as steps.
        var wire = roundTrip(new Client.ScenarioStarted(
                runId, "F/s", "F", "s", 1, DateTimeOffset.UtcNow,
                TotalSteps: 2,
                DeclaredSteps:
                [
                    new Client.DeclaredStepInfo("Given", "the events are published"),
                    new Client.DeclaredStepInfo("Then", "every aggregate matches")
                ]))
            .ShouldBeOfType<Wire.ScenarioStarted>();

        wire.TotalSteps.ShouldBe(2);
        wire.DeclaredSteps.ShouldNotBeNull();
        wire.DeclaredSteps.Select(x => $"{x.Keyword} {x.Text}").ShouldBe(
            ["Given the events are published", "Then every aggregate matches"]);
    }

    [Fact]
    public void a_scenario_started_without_a_narrative_still_round_trips()
    {
        // An older publisher's JSON has no member at all — additive means additive, and every
        // other authoring style declares nothing.
        roundTrip(new Client.ScenarioStarted(runId, "F/s", "F", "s", 1, DateTimeOffset.UtcNow))
            .ShouldBeOfType<Wire.ScenarioStarted>().DeclaredSteps.ShouldBeNull();
    }

    [Fact]
    public void a_step_carries_the_declared_sentence_it_ran_under()
    {
        var wire = roundTrip(new Client.StepStarted(
                runId, "F/s", "step-1", "Given", "the events are published",
                StepNumber: 1, TotalSteps: 2, ScenarioElapsedMs: 4, DeclaredStepNumber: 2))
            .ShouldBeOfType<Wire.StepStarted>();

        wire.DeclaredStepNumber.ShouldBe(2);
    }

    [Fact]
    public void a_step_under_no_declared_region_round_trips_as_null()
    {
        roundTrip(new Client.StepStarted(runId, "F/s", "step-1", "Given", "a calculator"))
            .ShouldBeOfType<Wire.StepStarted>().DeclaredStepNumber.ShouldBeNull();
    }

    [Fact]
    public void scenario_finished_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.ScenarioFinished(
                runId, "F/s", "PassOnRetry", 2, 950, "boom",
                TouchedTypes: [new Client.TouchedType("OpenWallet", "Wallets.OpenWallet", "Wallets")],
                At: at))
            .ShouldBeOfType<Wire.ScenarioFinished>();

        wire.Outcome.ShouldBe("PassOnRetry");
        wire.Attempts.ShouldBe(2);
        wire.DurationMs.ShouldBe(950);
        wire.ErrorMessage.ShouldBe("boom");
        wire.At.ShouldBe(at);
        var touched = wire.TouchedTypes.ShouldNotBeNull().ShouldHaveSingleItem();
        touched.Name.ShouldBe("OpenWallet");
        touched.FullName.ShouldBe("Wallets.OpenWallet");
        touched.AssemblyName.ShouldBe("Wallets");
    }

    [Fact]
    public void scenario_finished_without_evidence_still_round_trips()
    {
        // An old publisher's JSON has neither member — additive means additive. And null stays
        // null: nothing recorded is absence of evidence, never an empty list pretending to be one.
        var wire = roundTrip(new Client.ScenarioFinished(runId, "F/s", "CleanPass", 1, 10, null))
            .ShouldBeOfType<Wire.ScenarioFinished>();

        wire.TouchedTypes.ShouldBeNull();
        wire.At.ShouldBeNull();
    }

    [Fact]
    public void an_acceptance_verdict_round_trips()
    {
        // bobcat#545: the outcome, the lifecycle beside it, and the run's count of them.
        var scenario = roundTrip(new Client.ScenarioFinished(
                runId, "F/s", "AcceptanceFailing", 1, 10, "not yet", Lifecycle: "Acceptance"))
            .ShouldBeOfType<Wire.ScenarioFinished>();

        scenario.Outcome.ShouldBe("AcceptanceFailing");
        scenario.Lifecycle.ShouldBe("Acceptance");
        scenario.ErrorMessage.ShouldBe("not yet");

        var run = roundTrip(new Client.RunFinished(runId, 0, 1, 0, 0, 0, DateTimeOffset.UtcNow, AcceptanceFailing: 2))
            .ShouldBeOfType<Wire.RunFinished>();

        run.AcceptanceFailing.ShouldBe(2);
        run.ExitCode.ShouldBe(0);
    }

    [Fact]
    public void retry_scheduled_round_trips()
    {
        var wire = roundTrip(new Client.RetryScheduled(runId, "F/s", 2, "RetryInProcess", "flaky broker"))
            .ShouldBeOfType<Wire.RetryScheduled>();

        wire.NextAttempt.ShouldBe(2);
        wire.Disposition.ShouldBe("RetryInProcess");
        wire.Reason.ShouldBe("flaky broker");
    }

    [Fact]
    public void step_started_round_trips()
    {
        var wire = roundTrip(new Client.StepStarted(runId, "F/s", "step-1", "Given", "a calculator"))
            .ShouldBeOfType<Wire.StepStarted>();

        wire.StepId.ShouldBe("step-1");
        wire.Kind.ShouldBe("Given");
        wire.Text.ShouldBe("a calculator");
    }

    [Fact]
    public void step_finished_round_trips()
    {
        var wire = roundTrip(new Client.StepFinished(runId, "F/s", "step-1", "failed", 12, "assertion"))
            .ShouldBeOfType<Wire.StepFinished>();

        wire.Status.ShouldBe("failed");
        wire.DurationMs.ShouldBe(12);
        wire.ErrorMessage.ShouldBe("assertion");
    }

    [Fact]
    public void the_plan_rides_the_announcement_and_each_step_names_its_place_in_it()
    {
        // stoat#59/#60 on Bobcat's own records (0.28.0): the plan is known before a line runs, a
        // step names which planned call site it came from, and which runs of its text were inputs.
        var started = roundTrip(new Client.ScenarioStarted(runId, "F/s", "F", "s", 1, DateTimeOffset.UtcNow, TotalSteps: 2,
                PlannedSteps:
                [
                    new Client.PlannedStepInfo("Given", "Start with {value}", "CalculatorGrammar.StartWith", DeclaredStepNumber: 1),
                    new Client.PlannedStepInfo("", "The value should be {value}", "CalculatorGrammar.ValueIs")
                ]))
            .ShouldBeOfType<Wire.ScenarioStarted>();

        started.PlannedSteps.ShouldNotBeNull().Select(p => $"{p.Keyword}|{p.Template}|{p.Grammar}|{p.DeclaredStepNumber}")
            .ShouldBe(["Given|Start with {value}|CalculatorGrammar.StartWith|1", "|The value should be {value}|CalculatorGrammar.ValueIs|"]);

        var step = roundTrip(new Client.StepStarted(runId, "F/s", "StartWith", "Given", "Start with 3",
                StepNumber: 1, TotalSteps: 2, PlannedStepNumber: 1, Values: [new Client.StepValueSpan(11, 1)]))
            .ShouldBeOfType<Wire.StepStarted>();

        step.PlannedStepNumber.ShouldBe(1);
        step.Values.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new Wire.StepValueSpan(11, 1));
    }

    [Fact]
    public void a_table_literals_input_cell_round_trips_its_value()
    {
        // bobcat#396, Bobcat 0.30.0: a cell nothing judged carries its text in Value, not Actual.
        // This mirror lacked the field, so every decision table's input columns rendered empty.
        var wire = roundTrip(new Client.StepFinished(runId, "F/s", "sums", "passed", 2, null,
                Cells:
                [
                    new Client.StepCell("x", "ok", RowIndex: 0, Value: "2"),
                    new Client.StepCell("sum", "success", "4", "4", RowIndex: 0)
                ]))
            .ShouldBeOfType<Wire.StepFinished>();

        wire.Cells.ShouldNotBeNull().Select(c => $"{c.Name}:{c.Actual}:{c.Value}").ShouldBe(["x::2", "sum:4:"]);
    }

    /// <summary>
    /// Every property of every Bobcat wire record has a twin here. This mirror is hand-kept so
    /// Stoat references no Bobcat assembly, and the cost of that (bobcat 58635fa says so) is that a
    /// field Bobcat ADDS is invisible: it deserialises as nothing, no test fails, and the data is
    /// silently dropped. StepCell.Value was dropped exactly that way across a whole release.
    /// </summary>
    [Fact]
    public void the_mirror_has_every_field_bobcats_wire_records_carry()
    {
        var bobcat = typeof(Client.MonitorEvent).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace == typeof(Client.MonitorEvent).Namespace)
            .Where(t => t.GetMethod("<Clone>$") is not null) // records only
            .ToList();
        bobcat.ShouldNotBeEmpty();

        var missing = new List<string>();
        foreach (var record in bobcat)
        {
            var twin = typeof(Wire.MonitorEvent).Assembly.GetType($"{typeof(Wire.MonitorEvent).Namespace}.{record.Name}");
            if (twin is null)
            {
                // A whole event Stoat does not know is dropped at ingest too.
                if (record.IsSubclassOf(typeof(Client.MonitorEvent))) missing.Add(record.Name);
                continue;
            }

            var have = twin.GetProperties().Select(p => p.Name).ToHashSet();
            missing.AddRange(record.GetProperties()
                .Where(p => p.Name != "EqualityContract" && !have.Contains(p.Name))
                .Select(p => $"{record.Name}.{p.Name}"));
        }

        missing.ShouldBeEmpty("Bobcat's wire carries fields this mirror drops — add them to Contracts/MonitorEvents.cs and regenerate the TypeScript");
    }

    [Fact]
    public void a_step_report_round_trips_cells_logs_diagnostics_and_its_exception()
    {
        // stoat#58: everything a finished step can say, each field apart from the message.
        var wire = roundTrip(new Client.StepFinished(runId, "F/s", "Check", "failed", 2, "Sum: expected '6', got '8'",
                ScenarioElapsedMs: 5,
                Cells:
                [
                    new Client.StepCell("Sum", "failed", "6", "8"),
                    new Client.StepCell("Name", "success", "Bill", "Bill", Note: "matched", RowIndex: 0)
                ],
                Columns: ["Name"],
                Logs: ["computed 4 + 4"],
                Diagnostics: new Dictionary<string, string> { ["x"] = "4" },
                ExceptionType: "System.DivideByZeroException",
                StackTrace: "raw",
                StackFrames: ["at CalculatorGrammar.DivideByZero()"],
                HiddenStackFrames: 3))
            .ShouldBeOfType<Wire.StepFinished>();

        wire.Cells.ShouldNotBeNull().Select(c => $"{c.Name}:{c.Status}:{c.Expected}:{c.Actual}:{c.Note}:{c.RowIndex}")
            .ShouldBe(["Sum:failed:6:8::-1", "Name:success:Bill:Bill:matched:0"]);
        wire.Columns.ShouldBe(["Name"]);
        wire.Logs.ShouldBe(["computed 4 + 4"]);
        wire.Diagnostics.ShouldNotBeNull()["x"].ShouldBe("4");
        wire.ExceptionType.ShouldBe("System.DivideByZeroException");
        wire.StackTrace.ShouldBe("raw");
        wire.StackFrames.ShouldBe(["at CalculatorGrammar.DivideByZero()"]);
        wire.HiddenStackFrames.ShouldBe(3);
    }

    [Fact]
    public void step_progress_carries_the_cells_made_so_far()
    {
        // bobcat#387: a long table step reports the cells it has judged before it finishes.
        var wire = roundTrip(new Client.StepProgress(runId, "F/s", "Check", "row 2 of 3", 2, 3, 40,
                Cells: [new Client.StepCell("Sum", "success", "6", "6", RowIndex: 1)]))
            .ShouldBeOfType<Wire.StepProgress>();

        wire.Row.ShouldBe(2);
        wire.TotalRows.ShouldBe(3);
        wire.Cells.ShouldNotBeNull().ShouldHaveSingleItem().RowIndex.ShouldBe(1);
    }

    [Fact]
    public void lane_started_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.LaneStarted(runId, 2, ["F/a", "F/b"], at))
            .ShouldBeOfType<Wire.LaneStarted>();

        wire.Lane.ShouldBe(2);
        wire.Uids.ShouldBe(["F/a", "F/b"]);
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void lane_finished_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.LaneFinished(runId, 1, 7, true, at))
            .ShouldBeOfType<Wire.LaneFinished>();

        wire.Lane.ShouldBe(1);
        wire.Outcomes.ShouldBe(7);
        wire.Crashed.ShouldBeTrue();
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void resource_recycled_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.ResourceRecycled(runId, "rabbit", at))
            .ShouldBeOfType<Wire.ResourceRecycled>();

        wire.Resource.ShouldBe("rabbit");
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void worker_faulted_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.WorkerFaulted(runId, 3, "the worker exited with code 139", 139, "Segmentation fault", at))
            .ShouldBeOfType<Wire.WorkerFaulted>();

        wire.Lane.ShouldBe(3);
        wire.Fault.ShouldBe("the worker exited with code 139");
        wire.ExitCode.ShouldBe(139);
        wire.StandardError.ShouldBe("Segmentation fault");
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void a_worker_fault_outside_any_lane_round_trips_with_null_lane_and_no_exit_code()
    {
        // A one-test isolated process has no lane, and a worker that stopped responding without
        // dying has no exit code yet — both are facts, not gaps.
        var wire = roundTrip(new Client.WorkerFaulted(runId, null, "the worker stopped responding but is still running", null, null, DateTimeOffset.UtcNow))
            .ShouldBeOfType<Wire.WorkerFaulted>();

        wire.Lane.ShouldBeNull();
        wire.ExitCode.ShouldBeNull();
        wire.StandardError.ShouldBeNull();
    }

    [Fact]
    public void an_ingest_batch_of_mixed_events_deserializes_into_the_server_batch_shape()
    {
        var json = JsonSerializer.Serialize(new
        {
            events = new Client.MonitorEvent[]
            {
                new Client.RunStarted(runId, "Suite", "/repo", null, "mtp-host", DateTimeOffset.UtcNow, null),
                new Client.RunHeartbeat(runId, DateTimeOffset.UtcNow)
            }
        }, options);

        var batch = JsonSerializer.Deserialize<MonitorHost.IngestBatch>(json, options);

        batch.ShouldNotBeNull();
        batch.Events.Length.ShouldBe(2);
        batch.Events[0].ShouldBeOfType<Wire.RunStarted>();
        batch.Events[1].ShouldBeOfType<Wire.RunHeartbeat>();
    }

    [Fact]
    public void worker_started_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.WorkerStarted(runId, 2, "Lane", 4321, at))
            .ShouldBeOfType<Wire.WorkerStarted>();

        wire.Lane.ShouldBe(2);
        wire.Purpose.ShouldBe("Lane");
        wire.ProcessId.ShouldBe(4321);
        wire.At.ShouldBe(at);

        // The in-process client's shape: no lane slot, no pid.
        var isolated = roundTrip(new Client.WorkerStarted(runId, null, "Isolated", null, at))
            .ShouldBeOfType<Wire.WorkerStarted>();
        isolated.Lane.ShouldBeNull();
        isolated.ProcessId.ShouldBeNull();
    }

    [Fact]
    public void test_stalled_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.TestStalled(runId, "Orders/a", "Orders / a", 31_000, 0, 4321, at))
            .ShouldBeOfType<Wire.TestStalled>();

        wire.Uid.ShouldBe("Orders/a");
        wire.DisplayName.ShouldBe("Orders / a");
        wire.InFlightMs.ShouldBe(31_000);
        wire.Lane.ShouldBe(0);
        wire.ProcessId.ShouldBe(4321);
        wire.At.ShouldBe(at);
    }

    [Fact]
    public void run_progress_round_trips_with_null_memory_staying_null()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.RunProgress(
                runId, 252_000, 143, 275, 1, "Orders/a", "Orders / a", 94_000, 500L * 1024 * 1024, at))
            .ShouldBeOfType<Wire.RunProgress>();

        wire.ElapsedMs.ShouldBe(252_000);
        wire.Completed.ShouldBe(143);
        wire.Total.ShouldBe(275);
        wire.InFlight.ShouldBe(1);
        wire.LongestRunningUid.ShouldBe("Orders/a");
        wire.LongestRunningMs.ShouldBe(94_000);
        wire.PeakWorkerRssBytes.ShouldBe(500L * 1024 * 1024);
        wire.At.ShouldBe(at);

        // Sampling off: unmeasured travels as null, never zero.
        roundTrip(new Client.RunProgress(runId, 1000, 0, 1, 1, null, null, null, null, at))
            .ShouldBeOfType<Wire.RunProgress>().PeakWorkerRssBytes.ShouldBeNull();

        // An older publisher sends no verdict split (bobcat#767); the mirror says so with nulls.
        wire.Passed.ShouldBeNull();
        wire.Failed.ShouldBeNull();
        wire.Retried.ShouldBeNull();
        wire.WorkersBusy.ShouldBeNull();

        var split = roundTrip(new Client.RunProgress(
                runId, 252_000, 143, 275, 4, "Orders/a", "Orders / a", 94_000, null, at,
                Passed: 140, Failed: 3, Retried: 2, WorkersBusy: 4))
            .ShouldBeOfType<Wire.RunProgress>();
        split.Passed.ShouldBe(140);
        split.Failed.ShouldBe(3);
        split.Retried.ShouldBe(2);
        split.WorkersBusy.ShouldBe(4);
    }

    [Fact]
    public void test_started_round_trips()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.TestStarted(runId, "Acme.OrderTests.pays", "Acme.OrderTests.pays", 2, at))
            .ShouldBeOfType<Wire.TestStarted>();

        wire.Uid.ShouldBe("Acme.OrderTests.pays");
        wire.DisplayName.ShouldBe("Acme.OrderTests.pays");
        wire.Lane.ShouldBe(2);
        wire.At.ShouldBe(at);

        // A one-test isolated or recycled process has no lane slot — same rule as WorkerFaulted.
        roundTrip(new Client.TestStarted(runId, "t", "t", null, at))
            .ShouldBeOfType<Wire.TestStarted>().Lane.ShouldBeNull();
    }

    [Fact]
    public void test_finished_round_trips_with_the_framework_word_intact()
    {
        var at = DateTimeOffset.UtcNow;
        var wire = roundTrip(new Client.TestFinished(runId, "Acme.OrderTests.pays", "pays", "Skipped", 120, 0, at))
            .ShouldBeOfType<Wire.TestFinished>();

        wire.Uid.ShouldBe("Acme.OrderTests.pays");
        wire.DisplayName.ShouldBe("pays");
        // Never re-labelled by the publisher — the console owns that mapping, in one place.
        wire.State.ShouldBe("Skipped");
        wire.DurationMs.ShouldBe(120);
        wire.Lane.ShouldBe(0);
        wire.At.ShouldBe(at);

        // The supervisor never saw the in-progress update to measure from: unmeasured, not zero.
        roundTrip(new Client.TestFinished(runId, "t", "t", "Passed", null, null, at))
            .ShouldBeOfType<Wire.TestFinished>().DurationMs.ShouldBeNull();
    }
}
