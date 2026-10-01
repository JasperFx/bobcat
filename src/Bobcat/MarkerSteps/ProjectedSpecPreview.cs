using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;

namespace Bobcat;

/// <summary>
/// A projected specification shown <b>without running it</b> — the projected lane's answer to
/// <c>bobcat preview</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it could not exist before.</b> <see cref="PreviewRender.FromScenario"/> composes a plan
/// from a <c>FeatureDefinition</c>, a <c>ScenarioDefinition</c> and a fresh <c>Fixture</c> instance.
/// A projected test has none of the three: its steps are a test method's body. So the plan had to
/// become a fact the build records — <see cref="PlannedSteps"/> — and this reads it back.
/// </para>
/// <para>
/// <b>It renders through the same <see cref="PreviewRender"/> model as the Gherkin lane</b>, and for
/// the same reason the results do: two preview formats would mean improving one and forgetting the
/// other. The binding it shows is the <c>[BobcatStep]</c> helper each call resolves to, which is the
/// same question the Gherkin lane's bindings answer — "why did my step match that grammar".
/// </para>
/// <para>
/// <b>Templates, not sentences.</b> A preview shows <c>Start with {value}</c>, because at preview
/// time no argument has been evaluated. Filling the placeholders in would be describing a run that
/// never happened — the same rule that keeps a declared step from ever gaining a verdict.
/// </para>
/// </remarks>
public static class ProjectedSpecPreview
{
    /// <summary>
    /// Every projected specification this assembly registered, grouped by feature and ordered
    /// within it, ready to render.
    /// </summary>
    /// <remarks>
    /// Built from the union of both registries: a scenario appears if it declared marker comments,
    /// or planned grammar calls, or both. A test that did neither is not a specification and is not
    /// listed — the same rule <c>MarkerCommentSpecs</c> applies when it declines to register an
    /// unmarked test.
    /// </remarks>
    public static IReadOnlyList<PreviewRender> All()
    {
        var uids = DeclaredSteps.KnownScenarios
            .Concat(PlannedSteps.KnownScenarios)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal);

        return uids.Select(For).Where(x => x.Steps.Count > 0).ToList();
    }

    /// <summary>One scenario's preview, by its <c>{Feature}/{Scenario}</c> identity.</summary>
    public static PreviewRender For(string uid)
    {
        var slash = uid.LastIndexOf('/');
        var feature = slash > 0 ? uid[..slash] : "";
        var scenario = slash > 0 ? uid[(slash + 1)..] : uid;

        var declared = DeclaredSteps.For(uid);
        var planned = PlannedSteps.For(uid);

        // Merged by source line, the one ordering both of them carry — the same merge the results
        // render uses, so a preview and a result list the steps in the same order by construction.
        var rows = declared
            .Select(d => (d.Line, Step: narrativeRow(d)))
            .Concat(planned.Select(p => (p.Line, Step: plannedRow(p))))
            .OrderBy(x => x.Line)
            .Select(x => x.Step)
            .ToList();

        return new PreviewRender
        {
            Title = scenario,
            FeatureTitle = feature,
            Steps = rows
        };
    }

    private static PreviewStepRender narrativeRow(DeclaredStep declared)
        => new()
        {
            StepId = "d" + declared.Line,
            Kind = StepRender.KindOf(declared.Keyword),
            Keyword = declared.Keyword,
            IsNarrative = true,
            StepText = declared.Text
        };

    private static PreviewStepRender plannedRow(PlannedStep planned)
    {
        var dot = planned.Grammar.LastIndexOf('.');
        var type = dot > 0 ? planned.Grammar[..dot] : planned.Grammar;
        var method = dot > 0 ? planned.Grammar[(dot + 1)..] : planned.Grammar;

        return new PreviewStepRender
        {
            StepId = "p" + planned.Line,
            Kind = StepRender.KindOf(planned.Keyword),
            Keyword = planned.Keyword,
            StepText = planned.Template,

            // No arguments: a call site's argument VALUES are runtime facts, and the preview's job
            // is to say which grammar the step resolves to, not to guess what it will be handed.
            Binding = new StepBinding(type, method, planned.Template, [])
        };
    }
}
