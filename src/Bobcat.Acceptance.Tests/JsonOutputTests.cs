using System.Text.Json;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class JsonOutputTests
{
    private static async Task<JsonElement> RenderJson(string feature, string scenario, Func<Runtime.FeatureDefinition> define)
    {
        var results = await Specs.Run(define(), scenario);
        var spec = SpecRender.FromResults(scenario, results, feature);
        var json = JsonRenderer.RenderScenario(spec);
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task scalar_return_value_failure_is_structured()
    {
        var root = await RenderJson("Return Value", "Addition fails", Return_Value_Feature.Define);

        var step = root.GetProperty("steps").EnumerateArray().Single();
        step.GetProperty("status").GetString().ShouldBe("failed");

        var cell = step.GetProperty("cells").EnumerateArray().Single();
        cell.GetProperty("column").GetString().ShouldBe("result");
        cell.GetProperty("status").GetString().ShouldBe("failed");
        cell.GetProperty("expected").GetString().ShouldBe("6");
        cell.GetProperty("actual").GetString().ShouldBe("5");
        // No need to scrape a display string.
        cell.TryGetProperty("value", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task decision_table_failure_carries_row_and_column()
    {
        var root = await RenderJson("Decision Table", "Return-value column has a discrepancy", Decision_Table_Feature.Define);

        var step = root.GetProperty("steps").EnumerateArray().Single();
        var sv = step.GetProperty("setVerification");
        sv.GetProperty("columns").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "quantity", "price", "LineTotal" });

        // Find the failing cell by row+column directly — the whole point of the AI JSON.
        var failing = sv.GetProperty("rows").EnumerateArray()
            .SelectMany(r => r.GetProperty("cells").EnumerateArray())
            .Single(c => c.GetProperty("status").GetString() == "failed");

        failing.GetProperty("column").GetString().ShouldBe("LineTotal");
        failing.GetProperty("row").GetInt32().ShouldBe(1);
        failing.GetProperty("expected").GetString().ShouldBe("9.00");
        failing.GetProperty("actual").GetString().ShouldBe("8.00");
    }

    [Fact]
    public async Task decision_table_input_cells_keep_a_plain_value()
    {
        var root = await RenderJson("Decision Table", "Return-value columns all pass", Decision_Table_Feature.Define);

        var step = root.GetProperty("steps").EnumerateArray().Single();
        var firstRow = step.GetProperty("setVerification").GetProperty("rows").EnumerateArray().First();
        var quantity = firstRow.GetProperty("cells").EnumerateArray()
            .Single(c => c.GetProperty("column").GetString() == "quantity");

        // Input cells carry a plain value rather than expected/actual.
        quantity.GetProperty("value").GetString().ShouldBe("2");
        quantity.TryGetProperty("expected", out _).ShouldBeFalse();
    }
}
