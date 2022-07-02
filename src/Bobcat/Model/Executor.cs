using System.Diagnostics;

namespace Bobcat.Model;

public class Executor
{
    private readonly IContinuationRule[] _rules;
    private readonly Stopwatch _stopwatch;

    public Executor(IContinuationRule[] rules)
    {
        _rules = rules;
        _stopwatch = new Stopwatch();
    }

    public async Task Execute(IExecutionContext context)
    {
        var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(context.Timeout);
        
        _stopwatch.Restart();
        context.ExecutionStarted();

        try
        {
            foreach (var step in context.Steps)
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

        try
        {
            await step.Execute(context, result, cancellation.Token);
        }
        catch (Exception e)
        {
            result.MarkErrored(e, _stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            result.MarkEnded(_stopwatch.ElapsedMilliseconds);
            context.StepFinished(result);
        }

        if (ShouldStop(context, step, out var reason))
        {
            context.MarkCancelled(reason);
            return true;
        }

        return false;
    }

    internal bool ShouldStop(IExecutionContext context, IExecutionStep lastStep, out string reason)
    {
        foreach (var rule in _rules)
        {
            if (rule.ShouldStop(context, lastStep, out reason))
            {
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

}