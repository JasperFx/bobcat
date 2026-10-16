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
/// <b>Why the plan is a build fact.</b> A projected test's steps are a test method's body, so there
/// is nothing to compose a plan from at run time. The plan had to become a fact the build records —
/// <see cref="PlannedSteps"/> — and this reads it back.
/// </para>
/// <para>
/// <b>It renders through the <see cref="PreviewRender"/> model.</b> The binding it shows is the
/// <c>[BobcatStep]</c> helper each call resolves to, which answers "why did my step match that
/// grammar".
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
    /// Built from the union of the registries: a scenario appears if it declared marker comments,
    /// planned grammar calls, or scenario vocabulary calls (bobcat#625). A test that did none of those is not a specification and is not
    /// listed — the same rule <c>MarkerCommentSpecs</c> applies when it declines to register an
    /// unmarked test. A subclass of a shared test base is listed under its own identity, with the
    /// steps its base declares (bobcat#600).
    /// </remarks>
    public static IReadOnlyList<PreviewRender> All()
    {
        // A base class's own identity is not a specification when only its subclasses run it: an
        // abstract base is never bound, and its steps are shown under each subclass instead.
        var bases = DeclaredSteps.InheritedScenarios
            .Select(uid => DeclaredSteps.InheritedFrom(uid))
            .Where(uid => uid is not null && DeclaredSteps.BindingFor(uid) is null)
            .ToHashSet(StringComparer.Ordinal);

        var uids = DeclaredSteps.KnownScenarios
            .Concat(PlannedSteps.KnownScenarios)
            .Concat(VocabularySteps.KnownScenarios)
            .Concat(DeclaredSteps.InheritedScenarios)
            .Where(uid => !bases.Contains(uid))
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

        // An inherited test's steps are registered under its base class's identity (bobcat#600).
        var steps = DeclaredSteps.InheritedFrom(uid) ?? uid;
        var declared = DeclaredSteps.For(steps);
        var planned = PlannedSteps.For(steps);
        var vocabulary = VocabularySteps.For(steps);

        // Merged by source line, the one ordering both of them carry — the same merge the results
        // render uses, so a preview and a result list the steps in the same order by construction.
        var rows = declared
            .Select(d => (d.Line, Step: narrativeRow(d)))
            .Concat(planned.Select(p => (p.Line, Step: plannedRow(p))))
            .Concat(vocabulary.Select(v => (v.Line, Step: vocabularyRow(v))))
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

    // bobcat#625: the vocabulary method is the binding, the way a [BobcatStep] helper is a planned step's
    private static PreviewStepRender vocabularyRow(VocabularyStep step)
    {
        var dot = step.Grammar.LastIndexOf('.');

        return new PreviewStepRender
        {
            StepId = "v" + step.Line,
            Kind = StepRender.KindOf(step.Keyword),
            Keyword = step.Keyword,
            StepText = step.Text,
            Binding = new StepBinding(dot > 0 ? step.Grammar[..dot] : step.Grammar,
                dot > 0 ? step.Grammar[(dot + 1)..] : step.Grammar, step.Text)
        };
    }

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
            Binding = new StepBinding(type, method, planned.Template)
        };
    }
}
