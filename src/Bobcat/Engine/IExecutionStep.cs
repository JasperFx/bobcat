namespace Bobcat.Engine;

public interface IExecutionStep
{
    Task Execute(IStepContext context, StepResult result, CancellationToken token);
    string StepId { get; }
    StepKind StepKind { get; }
}