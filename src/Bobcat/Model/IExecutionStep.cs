namespace Bobcat.Model;

public interface IExecutionStep
{   
    Task Execute(IExecutionContext context, StepResult result, CancellationToken token);
    string StepId { get; }
}