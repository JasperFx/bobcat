using Bobcat;
using Bobcat.Engine;

namespace Bobcat.Acceptance.Tests;

/// <summary>A plain (non-Fixture) shared grammar module with its own state.</summary>
public class CounterModule
{
    private int _count;

    [Given("the counter starts at {int}")]
    public void Start(int n) => _count = n;

    [When("the counter increments")]
    public void Increment() => _count++;

    [Then("the counter should be {int}")]
    public int Counter() => _count;
}

/// <summary>A module that inherits Fixture, so it should receive the step context.</summary>
public class ContextProbeModule : Fixture
{
    [Check("the module received a context")]
    public bool HasContext() => Context != null;
}

/// <summary>
/// The capture two cooperating grammars agree on. Deliberately the ONLY thing
/// <see cref="ActingModule"/> and <see cref="AssertingModule"/> have in common — no shared base,
/// no reference between them, exactly the issue #212 phase-1 contract.
/// </summary>
public sealed record ActCapture(string Payload);

/// <summary>Performs an act and publishes its capture onto the scenario-state blackboard.</summary>
public class ActingModule
{
    [When("the acting grammar performs {string}")]
    public void Act(IStepContext context, string payload) => context.SetState(new ActCapture(payload));
}

/// <summary>Asserts on whatever act happened, knowing only the capture type.</summary>
public class AssertingModule
{
    [Then("the observed act should be {string}")]
    public string Observed(IStepContext context) => context.GetState<ActCapture>().Payload;

    [Check("no act was observed")]
    public bool NoActObserved(IStepContext context) => !context.TryGetState<ActCapture>(out _);
}

[IncludeGrammars(typeof(CounterModule), typeof(ContextProbeModule))]
[IncludeGrammars(typeof(ActingModule), typeof(AssertingModule))]
public class ComposedFixture : Fixture
{
    [Check("the fixture's own check passes")]
    public bool OwnCheck() => true;
}
