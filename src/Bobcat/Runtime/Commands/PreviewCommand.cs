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
        RenderAll(input.Runner, input.FeatureFlag, input.TagFlag, new CommandLineRenderer());

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
            renderer.RenderFeatureHeader(feature.Title);

            foreach (var scenario in runner.SelectScenarios(feature, tagFilter))
            {
                renderer.RenderPreview(PreviewRender.FromScenario(feature, scenario));
            }

            Console.WriteLine();
        }
    }
}
