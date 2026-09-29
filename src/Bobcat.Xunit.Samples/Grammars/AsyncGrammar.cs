namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// Storyteller 5's <c>AsyncOperationsFixture</c> — a grammar whose methods return <c>Task</c>,
/// <c>Task&lt;T&gt;</c> for a value check, and <c>Task&lt;bool&gt;</c> for a fact.
/// </summary>
/// <remarks>
/// <b>The step's duration is the helper's duration, not the time to hand back a Task.</b> That is
/// what <c>MarkerStepRuntime.Track</c> is for: the generated interceptor returns the original Task
/// untouched and closes the step by continuation, so awaiting never changes the caller's execution
/// shape and the measurement is still honest. Worth checking in the rendered output — an async step
/// reporting 0ms would mean the step closed before the work did.
/// </remarks>
public class AsyncGrammar
{
    private string _name = "Jeremy";

    [When("Perform a task asynchronously")]
    internal async Task PerformTask() => await Task.Delay(20);

    [Then("The current name should be {name}")]
    internal async Task CheckName(string name)
    {
        await Task.Delay(10);
        SpecAssert.Check("name", _name, name);
    }

    [Then("This condition should be true")]
    internal async Task IsConditionTrue()
    {
        await Task.Delay(5);
        SpecAssert.Fact(_name.Length > 0);
    }

    [When("Renaming to {name} fails because the service is down")]
    internal async Task RenameFails(string name)
    {
        await Task.Delay(5);
        throw new InvalidOperationException($"Cannot rename to '{name}' — the naming service is down");
    }
}
