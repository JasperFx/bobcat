using Bobcat;

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

[IncludeGrammars(typeof(CounterModule), typeof(ContextProbeModule))]
public class ComposedFixture : Fixture
{
    [Check("the fixture's own check passes")]
    public bool OwnCheck() => true;
}
