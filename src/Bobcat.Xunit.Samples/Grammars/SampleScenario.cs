namespace Bobcat.Xunit.Samples.Scenarios;

/// <summary>
/// A scenario vocabulary of your own: a base class whose members ARE the steps, each declaring its
/// Event Model role with <see cref="EventModelRoleAttribute"/> and, for an act, its trigger with
/// <see cref="EventModelTriggerAttribute"/>. The <c>docs/marker-steps.md</c> examples of roles stamped
/// from a test body (bobcat#462, #502, #614) run over it — the same shape WolverineFx.Bobcat's
/// <c>WolverineSpec</c> has, over an in-memory <see cref="SampleApp"/> instead of a host.
/// </summary>
/// <remarks>
/// It records its own steps through <c>ScenarioRecorder.Step</c>, the way a vocabulary in another package
/// does, rather than through <c>[Given]</c>/<c>[When]</c>/<c>[Then]</c>: its step text is written from the
/// arguments at run time.
/// </remarks>
public abstract class SampleScenario
{
    protected SampleApp App { get; } = new();

    // #region vocabulary-roles
    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]       // a type parameter: the type argument
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]        // a value parameter: each argument's type
    public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => inStep("Given", $"{typeof(TAggregate).Name} has already recorded {names(events)}",
            () => App.Append(id, events));

    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route))]     // the act's trigger kind and route
    public Task WhenPosted(object command, string route)
        => inStep("When", $"{command.GetType().Name} is posted to \"{route}\"", () => App.Handle(command));
    // #endregion vocabulary-roles

    /// <summary>
    /// The stream the act only looks up: the same step and the same append as <see cref="GivenEvents{TAggregate}"/>,
    /// stamped into the slice's <c>ReadsFrom</c> rather than its aggregates.
    /// </summary>
    [EventModelRole(SliceRole.Context, nameof(TContext))]
    [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
    public Task GivenContext<TContext>(Guid id, params object[] events) where TContext : class
        => inStep("Given", $"{typeof(TContext).Name} has already recorded {names(events)}",
            () => App.Append(id, events));

    [EventModelRole(SliceRole.ArrangedReadModel, nameof(T))]
    public Task GivenReadModel<T>(T document) where T : class, IDocument
        => inStep("Given", $"{typeof(T).Name} {document.Id} is stored", () => App.Store(document));

    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.MessageHandler)]
    public Task WhenReceived(object command)
        => inStep("When", $"{command.GetType().Name} is received", () => App.Handle(command));

    /// <summary>An act that names no command: a lambda against the application.</summary>
    [EventModelAct]
    public Task WhenActing(string description, Func<SampleApp, Task> act)
        => inStep("When", description, () => act(App));

    [EventModelRole(SliceRole.Event, nameof(events))]
    public void ThenEvents(params Type[] events)
    {
        using var step = ScenarioRecorder.Step("Then", $"{names(events)} is emitted");
        SpecAssert.Check("events", names(App.Appended.Select(x => x.GetType())), names(events));
    }

    [EventModelRole(SliceRole.Message, nameof(T))]
    public void ThenMessageSent<T>()
    {
        using var step = ScenarioRecorder.Step("Then", $"{typeof(T).Name} is sent");
        SpecAssert.Fact(App.Sent.OfType<T>().Any(), $"a {typeof(T).Name} was sent");
    }

    [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
    public void ThenStreamStarted<TAggregate>()
    {
        using var step = ScenarioRecorder.Step("Then", $"a new {typeof(TAggregate).Name} stream is started");
        SpecAssert.Fact(App.Started.Contains(typeof(TAggregate)), $"a {typeof(TAggregate).Name} stream was started");
    }

    private static string names(IEnumerable<object> items) => string.Join(", ", items.Select(x => x.GetType().Name));
    private static string names(IEnumerable<Type> types) => string.Join(", ", types.Select(x => x.Name));

    // A step that throws says so, then the failure carries on to the runner
    private static Task inStep(string keyword, string text, Action body)
        => inStep(keyword, text, () =>
        {
            body();
            return Task.CompletedTask;
        });

    private static async Task inStep(string keyword, string text, Func<Task> body)
    {
        var step = ScenarioRecorder.Step(keyword, text);
        try
        {
            await body();
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
