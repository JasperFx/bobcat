using Bobcat.Xunit.Samples.Application;

namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// Step helpers over <see cref="SchedulingApp"/> whose arguments are almost never literals — a
/// <see cref="Type"/>, a <see cref="Guid"/>, a command object — so every placeholder binds from the
/// value the helper was called with (issue #339). The <c>docs/marker-steps.md</c> examples.
/// </summary>
public class SchedulingSteps
{
    private readonly SchedulingApp _app = new();
    private int _status;

    // #region value-placeholders
    [Given("{aggregate} \"{id}\" has already recorded these events")]
    internal Task GivenEvents(Type aggregate, Guid id, params object[] events)
    {
        _app.Append(id, events);
        return Task.CompletedTask;
    }

    [When("{command} is posted to \"{route}\"")]
    internal Task WhenPosted(object command, string route)
    {
        _status = _app.Post(command, route);
        return Task.CompletedTask;
    }
    // #endregion value-placeholders

    [Then("{event} is emitted")]
    internal void ThenEmitted(Type @event)
        => SpecAssert.Check("event", _app.Emitted.Select(x => x.GetType().Name).LastOrDefault(), @event.Name);

    [Then("the response is {status}")]
    internal void TheResponseIs(int status) => SpecAssert.Check("status", _status, status);
}
