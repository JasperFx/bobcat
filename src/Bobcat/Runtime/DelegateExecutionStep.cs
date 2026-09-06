using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// An IExecutionStep backed by a delegate — the target for source-generated code.
/// No reflection: the generated lambda calls the fixture method directly.
/// </summary>
public class DelegateExecutionStep : IExecutionStep
{
    private readonly Func<IStepContext, StepResult, CancellationToken, Task> _execute;

    public DelegateExecutionStep(
        string stepId,
        StepKind stepKind,
        string stepText,
        Func<IStepContext, StepResult, CancellationToken, Task> execute)
    {
        StepId = stepId;
        StepKind = stepKind;
        StepText = stepText;
        _execute = execute;
    }

    public string StepId { get; }
    public StepKind StepKind { get; }

    /// <summary>
    /// The original Gherkin step text (e.g., "the left operand is 25").
    /// Used for rendering.
    /// </summary>
    public string StepText { get; }

    /// <summary>
    /// The compile-time step-to-method binding, emitted by the source generator for the
    /// <c>preview</c> command (issue #208). Null for steps that carry no generated metadata —
    /// code-first specs, hand-built definitions, or code generated before the property existed.
    /// </summary>
    public StepBinding? Binding { get; init; }

    public Task Execute(IStepContext context, StepResult result, CancellationToken token)
    {
        return _execute(context, result, token);
    }
}
