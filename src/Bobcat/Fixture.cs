using Bobcat.Engine;

namespace Bobcat;

/// <summary>
/// Base class for test fixtures. Subclass this and add methods with
/// [Given], [When], [Then], [Fact] attributes to define your test vocabulary.
/// </summary>
public abstract class Fixture
{
    /// <summary>
    /// Override to perform setup before each scenario.
    /// Prefer using [SetUp] attributed methods instead.
    /// </summary>
    public virtual Task SetUp() => Task.CompletedTask;

    /// <summary>
    /// Override to perform cleanup after each scenario (always runs, even on failure).
    /// Prefer using [TearDown] attributed methods instead.
    /// </summary>
    public virtual Task TearDown() => Task.CompletedTask;

    /// <summary>
    /// The step context for the currently executing scenario. Available during step execution.
    /// </summary>
    public IStepContext? Context { get; internal set; }
}
