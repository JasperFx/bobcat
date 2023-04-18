namespace Bobcat.Engine;

public interface IContinuationRule
{
    bool ShouldStop(IExecutionContext context, IExecutionStep lastStep, out string reason);
}