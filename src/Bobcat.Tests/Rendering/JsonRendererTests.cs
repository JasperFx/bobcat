using System.Text.Json;
using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Rendering;

public class JsonRendererTests
{
    public record Item(string Sku, int Quantity);

    private static JsonElement renderStep(StepResult step)
    {
        var render = StepRender.FromStepResult(step);
        var spec = new SpecRender { Title = "spec", Steps = { render } };
        var json = JsonRenderer.RenderScenario(spec);
        return JsonDocument.Parse(json).RootElement
            .GetProperty("steps").EnumerateArray().Single();
    }

    [Fact]
    public void set_verification_cells_are_structured_with_row_and_column()
    {
        var step = new StepResult("inv", 0) { StepText = "the inventory should be" };
        var actual = new[] { new Item("SKU-1", 90) };
        var expected = new[]
        {
            new Dictionary<string, string> { ["Sku"] = "SKU-1", ["Quantity"] = "85" }
        };

        SetVerificationComparer.Compare(actual, expected, new[] { "Sku" }, step);

        var stepJson = renderStep(step);
        var sv = stepJson.GetProperty("setVerification");

        var qty = sv.GetProperty("rows").EnumerateArray()
            .SelectMany(r => r.GetProperty("cells").EnumerateArray())
            .Single(c => c.GetProperty("column").GetString() == "Quantity");

        qty.GetProperty("row").GetInt32().ShouldBe(0);
        qty.GetProperty("status").GetString().ShouldBe("failed");
        qty.GetProperty("expected").GetString().ShouldBe("85");
        qty.GetProperty("actual").GetString().ShouldBe("90");
    }

    [Fact]
    public void note_is_carried_into_json()
    {
        var step = new StepResult("s", 0) { StepText = "approx" };
        step.MarkCells(new CellResult("total", ResultStatus.success)
        {
            Expected = "10.0", Actual = "10.01", Note = "±0.1"
        });

        var stepJson = renderStep(step);
        var cell = stepJson.GetProperty("cells").EnumerateArray().Single();
        cell.GetProperty("note").GetString().ShouldBe("±0.1");
        cell.GetProperty("expected").GetString().ShouldBe("10.0");
    }
}
