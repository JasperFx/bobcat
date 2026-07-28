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
/// Composes shared/library grammar modules into a fixture. The generator also scans the
/// listed module types for [Given]/[When]/[Then]/[Check] methods and matches their steps to
/// the feature, alongside the fixture's own. Repeatable/composable. Modules are instantiated
/// once per scenario; a module that inherits <see cref="Fixture"/> receives the step context.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class IncludeGrammarsAttribute : Attribute
{
    public Type[] Modules { get; }
    public IncludeGrammarsAttribute(params Type[] modules) => Modules = modules;
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
/// Polling modifier for eventual-consistency assertions. Retries the step's own success
/// criterion — return/out comparison until it matches, a <c>[Check]</c> until it returns
/// true, or a void action until it completes without throwing — attempting at t=0 then
/// every <see cref="PollAt"/> ms until <see cref="TimeoutMs"/> elapses. Exceptions during
/// the window are treated as "not ready yet" and retried; the last one surfaces on timeout.
/// Both values are milliseconds.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WaitForAttribute : Attribute
{
    public int TimeoutMs { get; }

    /// <summary>Poll interval in milliseconds. Defaults to 100ms.</summary>
    public int PollAt { get; set; } = 100;

    public WaitForAttribute(int timeoutMs) => TimeoutMs = timeoutMs;
}

/// <summary>
/// Overrides the naming convention to mark a fixture method as a per-scenario setup hook.
/// By convention any method named <c>BeforeEach</c> (or <c>BeforeEachAsync</c>) is one already.
/// Runs INSIDE the scenario's DI scope, so it can inject the same scoped services the steps see.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class BeforeEachAttribute : Attribute { }

/// <summary>
/// Overrides the naming convention to mark a fixture method as a per-scenario teardown hook
/// (always runs, even on failure). By convention any method named <c>AfterEach</c> (or
/// <c>AfterEachAsync</c>) is one already. Runs INSIDE the scenario's DI scope.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class AfterEachAttribute : Attribute { }

/// <summary>
/// Overrides the naming convention to mark a <b>static</b> fixture method as a once-per-feature
/// setup hook. By convention any static method named <c>BeforeAll</c> (or <c>BeforeAllAsync</c>)
/// is one already. Runs BEFORE any scenario scope exists, so it may inject the step context,
/// test resources, and root services — but not scoped services.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class BeforeAllAttribute : Attribute { }

/// <summary>
/// Overrides the naming convention to mark a <b>static</b> fixture method as a once-per-feature
/// teardown hook (always runs). By convention any static method named <c>AfterAll</c> (or
/// <c>AfterAllAsync</c>) is one already. Same injection rules as <see cref="BeforeAllAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class AfterAllAttribute : Attribute { }

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
