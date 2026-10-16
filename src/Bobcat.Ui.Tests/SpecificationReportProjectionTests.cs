using System.Text.Json;
using Bobcat.Ui.Contracts;
using Bobcat.Ui.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// The server half of a specification report (stoat#58, #59, #60): a step's cells, its plan
/// position and input values, and the exception it ended in all reach <c>GET /api/runs/{id}</c> —
/// and survive the archive, because a report that vanishes on restart was never a report.
/// </summary>
/// <remarks>
/// The wire itself is proved against Bobcat's own client records in <see cref="ContractRoundTripTests"/>
/// (Bobcat 0.28.0 carries every field). What stays here is the one wire fact a round trip cannot
/// show: a cell Bobcat serializes with only its required members still reads, defaults intact.
/// </remarks>
public class SpecificationReportProjectionTests : IDisposable
{
    private static readonly Guid run = Guid.Parse("9a1f1a1e-0000-0000-0000-000000000058");
    private const string uid = "Calculator/divides";
    private static readonly DateTimeOffset t0 = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"stoat-spec-report-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static RunDetail detail(MonitorRunRegistry registry)
        => RunEndpoints.Find(run, registry).ShouldBeOfType<Ok<RunDetail>>().Value.ShouldNotBeNull();

    private static MonitorEvent[] anAbortedProjectedSpec() =>
    [
        new RunStarted(run, "Calculator", "/repo", "main", "in-process", t0, 1),
        new ScenarioStarted(run, uid, "Calculator", "divides", 1, t0, TotalSteps: 4,
            PlannedSteps:
            [
                new PlannedStepInfo("Given", "Start with {value}", "CalculatorGrammar.StartWith"),
                new PlannedStepInfo("Then", "For X={x} and Y={y}, the Sum should be {sum}", "CalculatorGrammar.Check"),
                new PlannedStepInfo("When", "Divide by zero using integer maths", "CalculatorGrammar.DivideByZero"),
                new PlannedStepInfo("", "The value should be {value}", "CalculatorGrammar.ValueIs")
            ]),
        new StepStarted(run, uid, "StartWith", "Given", "Start with 3", StepNumber: 1, TotalSteps: 4,
            PlannedStepNumber: 1, Values: [new StepValueSpan(11, 1)]),
        new StepFinished(run, uid, "StartWith", "ok", 1, null),
        new StepStarted(run, uid, "Check", "Then", "For X=4 and Y=4, the Sum should be 6", StepNumber: 2,
            PlannedStepNumber: 2),
        new StepFinished(run, uid, "Check", "failed", 2, "Sum: expected '6', got '8'",
            Cells:
            [
                new StepCell("Sum", "failed", "6", "8"),
                new StepCell("Product", "success", "16", "16")
            ],
            Logs: ["computed 4 + 4"],
            Diagnostics: new Dictionary<string, string> { ["x"] = "4" }),
        new StepStarted(run, uid, "DivideByZero", "When", "Divide by zero using integer maths", StepNumber: 3,
            PlannedStepNumber: 3),
        new StepFinished(run, uid, "DivideByZero", "error", 3, "Attempted to divide by zero.",
            ExceptionType: "System.DivideByZeroException",
            StackTrace: "at Bobcat.Generated.__BobcatStep15()\n at CalculatorGrammar.DivideByZero()",
            StackFrames: ["at CalculatorGrammar.DivideByZero() in CalculatorGrammar.cs:107"],
            HiddenStackFrames: 1),
        new ScenarioFinished(run, uid, "Failed", 1, 20, "Attempted to divide by zero.", At: t0.AddSeconds(1)),
        new RunFinished(run, 1, 0, 1, 0, 0, t0.AddSeconds(2))
    ];

    [Fact]
    public void cells_and_the_step_report_reach_the_run_detail()
    {
        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(anAbortedProjectedSpec());

        var check = detail(registry).Scenarios.Single().Steps.Single(s => s.StepId == "Check");

        // Both cells, the green one included: a wrong sum must not hide a right product.
        check.Cells.Select(c => $"{c.Name}:{c.Status}:{c.Expected}:{c.Actual}")
            .ShouldBe(["Sum:failed:6:8", "Product:success:16:16"]);
        check.Logs.ShouldBe(["computed 4 + 4"]);
        check.Diagnostics["x"].ShouldBe("4");

        // A disagreement threw nothing, so there is no exception to render — only the message.
        check.Exception.ShouldBeNull();
        check.ErrorMessage.ShouldBe("Sum: expected '6', got '8'");
    }

    [Fact]
    public void spec_logs_on_scenario_finished_land_on_the_steps_they_were_written_in()
    {
        // bobcat#547: SpecLog lines are latched at the close, so they ride scenario_finished
        // keyed by step id rather than step_finished.
        var events = anAbortedProjectedSpec().ToList();
        var finished = events.OfType<ScenarioFinished>().Single();
        events[events.IndexOf(finished)] = finished with
        {
            Logs =
            [
                new StepLogInfo("StartWith", ["seeded 3"], 0),
                new StepLogInfo("DivideByZero", ["dividing"], 4),
                new StepLogInfo(null, ["between steps"], 0)
            ]
        };

        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(events.ToArray());

        var steps = detail(registry).Scenarios.Single().Steps;
        steps.Single(s => s.StepId == "StartWith").Logs.ShouldBe(["seeded 3"]);
        steps.Single(s => s.StepId == "DivideByZero").Logs.ShouldBe(["dividing", "…and 4 more lines"]);

        // A step no log names keeps what its own step_finished said.
        steps.Single(s => s.StepId == "Check").Logs.ShouldBe(["computed 4 + 4"]);
    }

    [Fact]
    public void an_exception_carries_its_type_and_filtered_frames_apart_from_the_message()
    {
        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(anAbortedProjectedSpec());

        var divide = detail(registry).Scenarios.Single().Steps.Single(s => s.StepId == "DivideByZero");

        var exception = divide.Exception.ShouldNotBeNull();
        exception.Type.ShouldBe("System.DivideByZeroException");
        exception.Frames.ShouldHaveSingleItem().ShouldContain("CalculatorGrammar.DivideByZero");
        exception.HiddenFrames.ShouldBe(1);
        // The raw stack stays for "show every frame".
        exception.StackTrace.ShouldNotBeNull().ShouldContain("__BobcatStep15");
    }

    [Fact]
    public void the_plan_reaches_the_run_detail_and_the_unreached_step_is_a_set_difference()
    {
        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(anAbortedProjectedSpec());

        var scenario = detail(registry).Scenarios.Single();
        scenario.PlannedSteps.Select(p => p.Keyword).ShouldBe(["Given", "Then", "When", ""]);
        scenario.Steps.Select(s => s.PlannedStepNumber).ShouldBe([1, 2, 3]);
        scenario.Steps[0].Values.ShouldHaveSingleItem().ShouldBe(new StepValueSpan(11, 1));

        registry.Read(run, r => r.Scenarios.Single().UnreachedPlannedSteps()).ShouldBe([4]);
    }

    [Fact]
    public void the_report_survives_a_restart()
    {
        using (var first = new MonitorRunRegistry(_dataPath))
        {
            first.Record(anAbortedProjectedSpec());
        }

        using var restarted = new MonitorRunRegistry(_dataPath);
        var scenario = detail(restarted).Scenarios.Single();

        scenario.PlannedSteps.Length.ShouldBe(4);
        scenario.Steps.Single(s => s.StepId == "Check").Cells.Length.ShouldBe(2);
        scenario.Steps.Single(s => s.StepId == "DivideByZero").Exception.ShouldNotBeNull()
            .HiddenFrames.ShouldBe(1);
    }

    [Fact]
    public void an_older_publisher_leaves_every_new_field_empty_rather_than_invented()
    {
        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(
        [
            new ScenarioStarted(run, uid, "Calculator", "divides", 1, t0),
            new StepStarted(run, uid, "s1", "Given", "Start with 3"),
            new StepFinished(run, uid, "s1", "failed", 1, "boom")
        ]);

        var scenario = detail(registry).Scenarios.Single();
        scenario.PlannedSteps.ShouldBeEmpty();
        var step = scenario.Steps.Single();
        step.Cells.ShouldBeEmpty();
        step.Values.ShouldBeEmpty();
        step.PlannedStepNumber.ShouldBeNull();
        step.Exception.ShouldBeNull();
    }

    [Fact]
    public void a_cell_with_only_its_required_members_reads_with_its_defaults_intact()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var sparse = JsonSerializer.Deserialize<StepCell>("""{"name":"Sum","status":"ok"}""", options)!;
        sparse.RowIndex.ShouldBe(-1);
        sparse.Expected.ShouldBeNull();
    }
}
