using System.Text.Json;
using Bobcat.Ui.Contracts;
using Bobcat.Ui;
using Bobcat.Ui.Agent;
using Bobcat.Ui.Runs;
using Bobcat.Ui.EventModel;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// stoat#58–#60 as an agent reads them. <c>failing_tests</c> and <c>failing_spec</c> are what an
/// agent diagnoses a red spec from, and a flattened error message was all they could say before:
/// the per-cell "expected 6, got 8", the green cells beside it, and an exception's own frames are
/// the diagnosis, not decoration.
/// </summary>
public class SpecReportToolsTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"stoat-spec-tools-{Guid.NewGuid():N}");
    private readonly MonitorRunRegistry _registry;
    private readonly EventModelStore _store;

    private static readonly Guid run = Guid.NewGuid();
    private const string uid = "Calculator/divides";

    public SpecReportToolsTests()
    {
        _registry = new MonitorRunRegistry(_dataPath);
        _store = new EventModelStore(_dataPath);

        var t0 = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        _registry.Record(
        [
            new RunStarted(run, "Calculator", "/repo", "main", "in-process", t0, 1),
            new ScenarioStarted(run, uid, "Calculator", "divides", 1, t0),
            new StepStarted(run, uid, "Check", "Then", "the Sum should be 6"),
            new StepFinished(run, uid, "Check", "failed", 2, "Sum: expected '6', got '8'",
                Cells:
                [
                    new StepCell("Sum", "failed", "6", "8"),
                    new StepCell("Product", "success", "16", "16")
                ],
                Logs: ["computed"]),
            // A step that spells no keyword: its name must not start with a space or a borrowed "Then".
            new StepStarted(run, uid, "Divide", "", "Divide by zero"),
            new StepFinished(run, uid, "Divide", "error", 1, "Attempted to divide by zero.",
                ExceptionType: "System.DivideByZeroException",
                StackTrace: "raw stack",
                StackFrames: ["at Calc.Divide() in Calc.cs:9"],
                HiddenStackFrames: 2),
            // A set verification with a missing row (stoat#63): a marker cell beside `ok` siblings
            // carrying the row's expected values, which is how Bobcat counts the row only once.
            new StepStarted(run, uid, "Customers", "Then", "the customers should be"),
            new StepFinished(run, uid, "Customers", "failed", 1, "1 row missing",
                Columns: ["Name", "Age"],
                Cells:
                [
                    new StepCell("Name", "success", "Bill", "Bill", RowIndex: 0),
                    new StepCell("missing-row", "missing", RowIndex: 1),
                    new StepCell("Name", "ok", Expected: "Ann", RowIndex: 1),
                    new StepCell("Age", "ok", Expected: "33", RowIndex: 1)
                ]),
            new ScenarioFinished(run, uid, "Failed", 1, 10, "Attempted to divide by zero."),
            new RunFinished(run, 1, 0, 1, 0, 0, t0.AddSeconds(1))
        ]);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static JsonElement parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void failing_tests_names_the_cells_that_disagreed_and_only_those()
    {
        var steps = parse(AgentQueries.FailingTests(_registry, run.ToString()))
            .GetProperty("failing")[0].GetProperty("failedSteps").EnumerateArray().ToArray();

        var check = steps.Single(s => s.GetProperty("name").GetString() == "Then the Sum should be 6");
        var cell = check.GetProperty("failedCells").EnumerateArray().ShouldHaveSingleItem();
        cell.GetProperty("name").GetString().ShouldBe("Sum");
        cell.GetProperty("expected").GetString().ShouldBe("6");
        cell.GetProperty("actual").GetString().ShouldBe("8");
        check.GetProperty("exception").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void failing_tests_carries_an_exception_as_its_type_and_filtered_frames()
    {
        var steps = parse(AgentQueries.FailingTests(_registry, run.ToString()))
            .GetProperty("failing")[0].GetProperty("failedSteps").EnumerateArray().ToArray();

        var divide = steps.Single(s => s.GetProperty("name").GetString() == "Divide by zero");
        var exception = divide.GetProperty("exception");
        exception.GetProperty("type").GetString().ShouldBe("System.DivideByZeroException");
        exception.GetProperty("frames")[0].GetString().ShouldBe("at Calc.Divide() in Calc.cs:9");
        exception.GetProperty("hiddenFrames").GetInt32().ShouldBe(2);
        // Filtered frames were sent, so the raw stack is not repeated for an agent to wade through.
        exception.GetProperty("stackTrace").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void failing_tests_says_which_row_a_set_verification_is_missing()
    {
        var steps = parse(AgentQueries.FailingTests(_registry, run.ToString()))
            .GetProperty("failing")[0].GetProperty("failedSteps").EnumerateArray().ToArray();

        var customers = steps.Single(s => s.GetProperty("name").GetString() == "Then the customers should be");
        // Only the marker is a failure — the siblings are the row's values, folded into it.
        var marker = customers.GetProperty("failedCells").EnumerateArray().ShouldHaveSingleItem();
        marker.GetProperty("name").GetString().ShouldBe("missing-row");
        marker.GetProperty("row").GetInt32().ShouldBe(1);
        marker.GetProperty("values").GetProperty("Name").GetString().ShouldBe("Ann");
        marker.GetProperty("values").GetProperty("Age").GetString().ShouldBe("33");
    }

    [Fact]
    public void failing_spec_shows_every_cell_green_ones_included()
    {
        var steps = parse(AgentQueries.FailingSpec(_registry, _store, run.ToString(), uid))
            .GetProperty("steps").EnumerateArray().ToArray();

        var check = steps[0];
        check.GetProperty("cells").EnumerateArray()
            .Select(c => $"{c.GetProperty("name").GetString()}:{c.GetProperty("status").GetString()}")
            .ShouldBe(["Sum:failed", "Product:success"]);
        check.GetProperty("cells")[0].GetProperty("row").ValueKind.ShouldBe(JsonValueKind.Null);
        check.GetProperty("logs")[0].GetString().ShouldBe("computed");

        // No keyword, no leading space.
        steps[1].GetProperty("name").GetString().ShouldBe("Divide by zero");
        steps[1].GetProperty("cells").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
