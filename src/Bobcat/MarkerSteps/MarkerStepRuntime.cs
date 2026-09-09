namespace Bobcat;

/// <summary>
/// The one runtime helper generated interceptors call (issue #110). Kept small and public because
/// generated code has to name it, and kept here rather than inlined into the generator so its
/// behaviour is testable and can change without regenerating anybody's code.
/// </summary>
public static class MarkerStepRuntime
{
    /// <summary>
    /// End <paramref name="step"/> when <paramref name="task"/> completes — successfully or not.
    /// </summary>
    /// <remarks>
    /// A step around an async helper must measure the helper's work, not the microsecond it takes
    /// to hand back a Task. Awaiting inside the interceptor would change the caller's execution
    /// shape, so the step is closed by continuation instead and the original Task is returned
    /// untouched.
    /// </remarks>
    public static async Task Track(Task task, IDisposable step)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The step's own verdict, not the scenario's. A helper that threw is the step that
            // failed, and this continuation is the only place that is still knowable.
            (step as IStepHandle)?.Fail(e);
            throw;
        }
        finally
        {
            step.Dispose();
        }
    }

    /// <summary>The <see cref="Task{T}"/> twin — the result flows through unchanged.</summary>
    public static async Task<T> Track<T>(Task<T> task, IDisposable step)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            (step as IStepHandle)?.Fail(e);
            throw;
        }
        finally
        {
            step.Dispose();
        }
    }
}
