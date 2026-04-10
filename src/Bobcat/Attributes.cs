namespace Bobcat;

/// <summary>
/// Marks a fixture method as a Given step (data setup).
/// Uses Gherkin Expression syntax for the pattern.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class GivenAttribute : StepAttribute
{
    public GivenAttribute(string expression) : base(expression) { }
}

/// <summary>
/// Marks a fixture method as a When step (action under test).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WhenAttribute : StepAttribute
{
    public WhenAttribute(string expression) : base(expression) { }
}

/// <summary>
/// Marks a fixture method as a Then step (assertion).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class ThenAttribute : StepAttribute
{
    public ThenAttribute(string expression) : base(expression) { }
}

/// <summary>
/// Base class for step attributes. Carries the Gherkin expression pattern
/// and maps to a StepKind for failure classification.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public abstract class StepAttribute : Attribute
{
    public string Expression { get; }

    protected StepAttribute(string expression)
    {
        Expression = expression;
    }
}

/// <summary>
/// Marks a method to run before each scenario in a fixture.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class SetUpAttribute : Attribute { }

/// <summary>
/// Marks a method to run after each scenario in a fixture (always runs, even on failure).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class TearDownAttribute : Attribute { }

/// <summary>
/// Marks a method as a boolean check — a Then step that returns bool (true = pass, false = fail).
/// Named "Check" to avoid collision with xUnit's [Fact].
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class CheckAttribute : StepAttribute
{
    public CheckAttribute(string expression) : base(expression) { }
}
