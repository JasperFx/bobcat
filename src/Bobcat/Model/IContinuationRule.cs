namespace Bobcat.Model;

public interface IContinuationRule
{
    bool ShouldStop(IExecutionContext context, IExecutionStep lastStep, out string reason);
}