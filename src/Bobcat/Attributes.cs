using System.Text.RegularExpressions;

namespace Bobcat;

/// <summary>
/// Sets the title used to match this fixture to a Gherkin Feature.
/// If not specified, the title is derived from the class name
/// (e.g., OrderAggregateFixture → "Order Aggregate").
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class FixtureTitleAttribute : Attribute
{
    public string Title { get; }
    public FixtureTitleAttribute(string title) => Title = title;
}

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

/// <summary>
/// Marks a step method as accepting table data. Each row in the table
/// becomes a separate invocation of the method.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class TableAttribute : Attribute { }

/// <summary>
/// Marks a step method as a decision table. The accompanying data table is matched
/// positionally per row: columns whose names match input parameters supply inputs,
/// and the remaining columns (matched to <c>out</c> parameters or the method's return
/// value) are <b>expected</b> outputs compared via the type-aware checker. Input cells
/// render plain; expected cells are colored by pass/fail.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class DecisionTableAttribute : Attribute { }

/// <summary>
/// Marks a step method's return value as the actual value compared against an expected
/// capture/column. Optional — a non-void <c>[Then]</c> method is treated as a
/// return-value verification by convention. Use to set an explicit column name in a
/// decision table when the method name is not the desired column.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.ReturnValue)]
public class ExpectedAttribute : Attribute
{
    /// <summary>Optional decision-table column name that maps to the return value.</summary>
    public string? Column { get; set; }

    public ExpectedAttribute() { }
    public ExpectedAttribute(string column) => Column = column;
}

/// <summary>
/// Marks a Then method as a set verification step. The method must return
/// IEnumerable of some type. Bobcat compares the returned collection against
/// expected table data, producing per-cell diffs.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class SetVerificationAttribute : Attribute
{
    /// <summary>
    /// Comma-separated column names that uniquely identify a row for matching.
    /// </summary>
    public string KeyColumns { get; set; } = "";
}

/// <summary>
/// Overrides the value checker used for a comparison. The supplied type must implement
/// <c>IValueChecker&lt;T&gt;</c> for the value being checked and have a parameterless
/// constructor. Highest precedence in the checker resolution chain.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Property)]
public class ComparisonAttribute : Attribute
{
    public Type CheckerType { get; }
    public ComparisonAttribute(Type checkerType) => CheckerType = checkerType;
}

/// <summary>
/// Compares numeric values with an absolute tolerance instead of exact equality.
/// Flows into <c>CheckOptions.Tolerance</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Property)]
public class ApproxAttribute : Attribute
{
    public double Tolerance { get; }
    public ApproxAttribute(double tolerance) => Tolerance = tolerance;
}
