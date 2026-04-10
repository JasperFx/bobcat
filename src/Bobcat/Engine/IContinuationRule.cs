namespace Bobcat.Engine;

public interface IContinuationRule
{
    bool ShouldStop(IExecutionContext context, IExecutionStep lastStep, StepResult result, out string reason);
}

/// <summary>
/// Stops execution when a step has a Critical or Catastrophic failure.
/// Assertion failures (wrong value in a Then step) are allowed to continue.
/// </summary>
public class FailureLevelContinuationRule : IContinuationRule
{
    public bool ShouldStop(IExecutionContext context, IExecutionStep lastStep, StepResult result, out string reason)
    {
        switch (result.FailureLevel)
        {
            case FailureLevel.Critical:
                reason = $"Critical failure in step '{lastStep.StepId}': aborting scenario";
                return true;

            case FailureLevel.Catastrophic:
                reason = $"Catastrophic failure in step '{lastStep.StepId}': stopping all execution";
                return true;

            default:
                reason = string.Empty;
                return false;
        }
    }
}
