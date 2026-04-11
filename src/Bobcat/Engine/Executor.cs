using System.Diagnostics;
using Bobcat.Runtime;

namespace Bobcat.Engine;

public class Executor
{
    private readonly IContinuationRule[] _rules;
    private readonly IExecutionObserver _observer;
    private readonly Stopwatch _stopwatch;

    public Executor(IContinuationRule[] rules, IExecutionObserver? observer = null)
    {
        _rules = rules;
        _observer = observer ?? NullObserver.Instance;
        _stopwatch = new Stopwatch();
    }

    public async Task Execute(ExecutionPlan plan, IExecutionContext context)
    {
        var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(plan.Timeout);

        _stopwatch.Restart();
        context.ExecutionStarted();

        try
        {
            foreach (var step in plan.Steps)
            {
                if (cancellation.IsCancellationRequested)
                {
                    context.TimedOut(_stopwatch.ElapsedMilliseconds);
                    break;
                }

                if (await executeStep(context, step, cancellation))
                {
                    break;
                }
            }

            context.ExecutionFinished(_stopwatch.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            context.ExecutionFailed(e, _stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            _stopwatch.Stop();
        }
    }

    private async Task<bool> executeStep(IExecutionContext context, IExecutionStep step,
        CancellationTokenSource cancellation)
    {
        var result = context.StepStarted(step, _stopwatch.ElapsedMilliseconds);

        var stepText = step is DelegateExecutionStep del ? del.StepText : step.StepId;
        _observer.StepStarted(step.StepId, step.StepKind, stepText);

        try
        {
            await step.Execute(context, result, cancellation.Token);

            // Auto-mark success if the step completed without setting a status
            if (result.StepStatus == ResultStatus.ok)
            {
                result.MarkSuccess();
            }
        }
        catch (Exception e)
        {
            result.MarkErrored(e, _stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            result.MarkEnded(_stopwatch.ElapsedMilliseconds);
            context.StepFinished(result);
            _observer.StepFinished(result);
        }

        if (ShouldStop(context, step, result, out var reason))
        {
            context.MarkCancelled(reason);
            return true;
        }

        return false;
    }

    internal bool ShouldStop(IExecutionContext context, IExecutionStep lastStep, StepResult result, out string reason)
    {
        foreach (var rule in _rules)
        {
            if (rule.ShouldStop(context, lastStep, result, out reason))
            {
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }
}
