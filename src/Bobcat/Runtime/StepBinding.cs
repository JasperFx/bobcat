namespace Bobcat.Runtime;

/// <summary>
/// The compile-time binding of one Gherkin step to the fixture method it matched — which
/// method, via which Cucumber expression, and where each parameter's value comes from.
/// Computed by the source generator (it is the matcher), carried on
/// <see cref="DelegateExecutionStep.Binding"/> so the <c>preview</c> command can show the one
/// thing reading the <c>.feature</c> file cannot: why a step ran the code it ran (issue #208).
/// </summary>
public class StepBinding
{
    public StepBinding(string declaringType, string method, string expression, StepBindingArgument[] arguments)
    {
        DeclaringType = declaringType;
        Method = method;
        Expression = expression;
        Arguments = arguments;
    }

    /// <summary>The fixture, grammar-module or [TableGrammar] class declaring the method, fully qualified.</summary>
    public string DeclaringType { get; }

    /// <summary>The method the step text matched.</summary>
    public string Method { get; }

    /// <summary>The Cucumber expression (or table-grammar step text) that won the match.</summary>
    public string Expression { get; }

    /// <summary>One entry per parameter, in declaration order — plus a trailing pseudo-entry
    /// for a compared return value.</summary>
    public StepBindingArgument[] Arguments { get; }

    /// <summary><see cref="DeclaringType"/> without namespace qualification, for rendering.</summary>
    public string DeclaringTypeName
    {
        get
        {
            var name = DeclaringType.StartsWith("global::", StringComparison.Ordinal)
                ? DeclaringType.Substring("global::".Length)
                : DeclaringType;
            var lastDot = name.LastIndexOf('.');
            return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
        }
    }
}

/// <summary>Where one parameter of a bound step method gets its value.</summary>
public class StepBindingArgument
{
    public StepBindingArgument(string name, string value, StepArgumentSource source)
    {
        Name = name;
        Value = value;
        Source = source;
    }

    /// <summary>The parameter name (or the result column name for a compared return value).</summary>
    public string Name { get; }

    /// <summary>The capture text, column/header name, or service type — whatever
    /// <see cref="Source"/> says this is.</summary>
    public string Value { get; }

    public StepArgumentSource Source { get; }
}

/// <summary>The kinds of value a step method parameter can be bound from.</summary>
public enum StepArgumentSource
{
    /// <summary>A Cucumber capture from the step text; <c>Value</c> is the captured text.</summary>
    Capture,

    /// <summary>A data-table column bound by header name; <c>Value</c> is the header.</summary>
    TableColumn,

    /// <summary>The step's trailing doc string.</summary>
    DocString,

    /// <summary>The whole trailing data table as a <c>StepTable</c>.</summary>
    Table,

    /// <summary>Injected from the scenario scope (or a step context / resource);
    /// <c>Value</c> is the type asked for.</summary>
    Service,

    /// <summary>An expected value the step compares against — an <c>out</c> parameter, a
    /// compared return value, or a decision-table output column.</summary>
    Expected,

    /// <summary>Nothing supplied the parameter; it receives <c>default</c>.</summary>
    Default
}
