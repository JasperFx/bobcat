using Bobcat.Engine;

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

    /// <summary>
    /// A <b>Fact's</b> verdict: fail <paramref name="step"/> when <paramref name="result"/> is false,
    /// and hand the answer back unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Storyteller's Fact is a <c>bool</c>-returning grammar whose answer IS the verdict, and a
    /// projected test reaches one by simply calling it. Before this the return value was handed
    /// straight back to the caller and nothing looked at it, so a step that answered <c>false</c>
    /// rendered green — a specification that could not fail.
    /// </para>
    /// <para>
    /// <b>No message.</b> A bare fact has nothing to say beyond "not true", and the red line already
    /// says that; inventing a sentence for it is the noise Storyteller's own
    /// <c>StoryTellerAssert</c> existed to replace. A grammar with something useful to report calls
    /// <see cref="SpecAssert.Fact(bool, string)"/> instead and supplies the reason.
    /// </para>
    /// <para>
    /// The step is <b>not</b> disposed here — the caller's <c>finally</c> owns that, so the duration
    /// is the helper's real duration whether the fact held or not.
    /// </para>
    /// </remarks>
    public static bool Fact(bool result, IDisposable step)
    {
        if (!result) (step as IStepHandle)?.Fail(new SpecAssertionException(""));
        return result;
    }

    /// <summary>
    /// The awaitable Fact — <c>Task&lt;bool&gt;</c>, which Storyteller had no equivalent of because it
    /// predates async/await. The step ends when the work ends, so it carries a real duration.
    /// </summary>
    public static async Task<bool> TrackFact(Task<bool> task, IDisposable step)
    {
        try
        {
            return Fact(await task.ConfigureAwait(false), step);
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
