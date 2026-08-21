using System.Text;
using Bobcat.CodeFirst;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #105's acceptance criterion: a code-first specification and its Gherkin twin render to the
/// same <see cref="SpecRender"/> shape — same titles, step kinds and texts, statuses, comparison
/// cells and set-verification table — so anything downstream (console, JSON, the viewer, an MTP
/// node) cannot tell which way the scenario was written.
/// </summary>
public class CodeFirstTwinTests
{
    private const string scenario = "Depositing into an account";

    [Fact]
    public async Task a_code_first_specification_and_its_gherkin_twin_render_to_the_same_shape()
    {
        var gherkin = await Specs.Run(Code_First_Twin_Feature.Define(), scenario);
        var codeFirst = await Specs.Run(SpecificationFeature.Build<CodeFirstTwinSpecification>(), scenario);

        var gherkinRender = SpecRender.FromResults(scenario, gherkin, "Code First Twin");
        var codeFirstRender = SpecRender.FromResults(scenario, codeFirst, "Code First Twin");

        gherkinRender.Succeeded.ShouldBeTrue();
        codeFirstRender.Succeeded.ShouldBeTrue();

        shape(codeFirstRender).ShouldBe(shape(gherkinRender));
    }

    [Fact]
    public void the_feature_titles_agree_too()
    {
        SpecificationFeature.Build<CodeFirstTwinSpecification>().Title.ShouldBe(Code_First_Twin_Feature.Define().Title);
    }

    /// <summary>
    /// Everything a renderer shows, minus the two things that legitimately differ: <c>StepId</c>
    /// (the generator keys it on the matched method name, code-first on position; nothing downstream
    /// keys off it) and durations.
    /// </summary>
    private static string shape(SpecRender render)
    {
        var text = new StringBuilder();
        text.AppendLine($"{render.FeatureTitle} / {render.Title} / {render.Succeeded} / {render.Counts}");

        foreach (var step in render.Steps)
        {
            text.AppendLine($"{step.Kind} | {step.StepText} | {step.Status} | {step.FailureLevel} | {step.ErrorMessage}");

            foreach (var cell in step.Cells)
                text.AppendLine($"  cell {cell.Name} | {cell.Status} | {cell.Expected} | {cell.Actual} | {cell.Note} | {cell.DisplayText}");

            if (step.SetVerification is { } table)
            {
                text.AppendLine($"  columns {string.Join(",", table.Columns)}");
                foreach (var row in table.Rows)
                {
                    text.AppendLine($"  row {row.RowType} | {row.Description} | " +
                                    string.Join(" ; ", row.Cells.Select(c => $"{c.Column}={c.DisplayText}:{c.Status}:{c.Expected}:{c.Actual}")));
                }
            }
        }

        return text.ToString();
    }
}
