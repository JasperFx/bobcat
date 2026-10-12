using System.Text.Json;
using Bobcat.Rendering;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// <c>JsonOutputTests</c> from the projected lane: the agent-facing JSON of a recorded scenario
/// carries the same structured cells as one the Gherkin executor produced, because both go through
/// <see cref="SpecRender"/> into <see cref="JsonRenderer.RenderScenario"/>.
/// </summary>
public class JsonOutputTests
{
    private static JsonElement json(SpecRender render) => JsonDocument.Parse(JsonRenderer.RenderScenario(render)).RootElement;

    [Fact]
    public void a_scalar_value_failure_is_structured()
    {
        var grammar = new ValueCheckTests.ValueGrammar();
        var (_, render) = Projected.Run("Addition fails", () => grammar.Plus(2, 3, 6));

        var step = json(render).GetProperty("steps").EnumerateArray().Single();
        step.GetProperty("status").GetString().ShouldBe("failed");

        var cell = step.GetProperty("cells").EnumerateArray().Single();
        cell.GetProperty("column").GetString().ShouldBe("sum");
        cell.GetProperty("status").GetString().ShouldBe("failed");
        cell.GetProperty("expected").GetString().ShouldBe("6");
        cell.GetProperty("actual").GetString().ShouldBe("5");

        // No display string to scrape.
        cell.TryGetProperty("value", out _).ShouldBeFalse();
    }

    private static async Task<JsonElement> lineTotals(string table)
    {
        var grammar = new DecisionTableTests.LineGrammar();
        var (_, render) = await Projected.RunAsync("line totals", () => grammar.TheLineTotalsAreCalculated(table));
        return json(render).GetProperty("steps").EnumerateArray().Single();
    }

    [Fact]
    public async Task a_decision_table_failure_carries_its_row_and_column()
    {
        var step = await lineTotals("""
            | quantity | price | LineTotal |
            | 2        | 10.00 | 20.00     |
            | 4        | 2.00  | 9.00      |
            """);

        var sv = step.GetProperty("setVerification");
        sv.GetProperty("columns").EnumerateArray().Select(e => e.GetString()).ShouldBe(["quantity", "price", "LineTotal"]);

        var failing = sv.GetProperty("rows").EnumerateArray()
            .SelectMany(r => r.GetProperty("cells").EnumerateArray())
            .Single(c => c.GetProperty("status").GetString() == "failed");

        failing.GetProperty("column").GetString().ShouldBe("LineTotal");
        failing.GetProperty("row").GetInt32().ShouldBe(1);
        failing.GetProperty("expected").GetString().ShouldBe("9.00");
        failing.GetProperty("actual").GetString().ShouldBe("8.00");
    }

    [Fact]
    public async Task a_decision_tables_input_cells_keep_a_plain_value()
    {
        var step = await lineTotals("""
            | quantity | price | LineTotal |
            | 2        | 10.00 | 20.00     |
            """);

        var quantity = step.GetProperty("setVerification").GetProperty("rows").EnumerateArray().First()
            .GetProperty("cells").EnumerateArray()
            .Single(c => c.GetProperty("column").GetString() == "quantity");

        quantity.GetProperty("value").GetString().ShouldBe("2");
        quantity.TryGetProperty("expected", out _).ShouldBeFalse();
    }
}
