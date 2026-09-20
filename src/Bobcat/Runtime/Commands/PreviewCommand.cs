using Bobcat.Rendering;
using JasperFx.CommandLine;

namespace Bobcat.Runtime.Commands;

/// <summary>
/// Renders features and scenarios with their step BINDINGS without executing anything
/// (issue #208): which fixture method each step matched and where every parameter's value
/// comes from — a debugging tool for "why did my step match the wrong grammar". Never starts
/// a resource: the plan is pure in-memory composition, built before <c>StartAll</c> would run.
/// </summary>
[Description("Preview features and their step bindings without executing anything", Name = "preview")]
public class PreviewCommand : JasperFxAsyncCommand<BobcatInput>
{
    public override Task<bool> Execute(BobcatInput input)
    {
        // Issue #369: an empty selection is reported rather than rendered as nothing at all.
        var empty = input.Runner.DescribeEmptySelection(input.FeatureFlag, input.TagFlag);
        if (empty != null) Console.WriteLine(empty);
        else RenderAll(input.Runner, input.FeatureFlag, input.TagFlag, new CommandLineRenderer());

        input.ExitCode = 0;
        return Task.FromResult(true);
    }

    /// <summary>
    /// The preview body, shared with the interactive command's per-selection preview action.
    /// </summary>
    internal static void RenderAll(
        BobcatRunner runner, string? featureFilter, string? tagFilter, CommandLineRenderer renderer)
    {
        foreach (var feature in runner.SelectFeatures(featureFilter))
        {
            var scenarios = runner.SelectScenarios(feature, tagFilter).ToArray();

            // Same reason as ListCommand: a feature whose scenarios a tag filter removed should
            // not render a header suggesting it has none (issue #370).
            if (scenarios.Length == 0) continue;

            renderer.RenderFeatureHeader(feature.Title);

            foreach (var scenario in scenarios)
            {
                renderer.RenderPreview(PreviewRender.FromScenario(feature, scenario));
            }

            Console.WriteLine();
        }
    }
}
