using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat.Rendering;

/// <summary>
/// A scenario's planned steps rendered without executing it (issue #208), each with the binding
/// the generator recorded. Built by <c>ProjectedSpecPreview</c> from the generated plan.
/// </summary>
public class PreviewRender
{
    public string Title { get; init; } = "";
    public string? FeatureTitle { get; init; }
    public string[] Tags { get; init; } = [];
    public List<PreviewStepRender> Steps { get; init; } = new();

    /// <summary>
    /// Set when the plan could not even be composed. The preview reports it instead of crashing.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>One planned step with its compile-time binding, when the step carries one.</summary>
public class PreviewStepRender
{
    public string StepId { get; init; } = "";
    public StepKind Kind { get; init; }

    /// <summary>
    /// The keyword as written, when it is known — including <c>And</c>, and including the empty
    /// string for a grammar that spells no keyword at all. Null falls back to <see cref="Kind"/>.
    /// </summary>
    public string? Keyword { get; init; }

    /// <summary>
    /// A sentence the author wrote as a marker comment rather than a step that will execute. It has
    /// no binding and never will; it is the narrative the executing steps sit under.
    /// </summary>
    public bool IsNarrative { get; init; }

    public string StepText { get; init; } = "";
    public StepBinding? Binding { get; init; }

}
