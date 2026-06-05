using System;
using System.Collections.Generic;

namespace Bobcat.Generators;

/// <summary>
/// Compile-time model of a discovered fixture class.
/// </summary>
public class FixtureInfo
{
    public string ClassName { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Title { get; set; } = "";
    public string FullyQualifiedName { get; set; } = "";
    public List<StepMethodInfo> StepMethods { get; set; } = new();
}

/// <summary>
/// Compile-time model of a step method on a fixture.
/// </summary>
public class StepMethodInfo
{
    public string MethodName { get; set; } = "";
    public string Expression { get; set; } = "";
    public string StepKind { get; set; } = ""; // "Given", "When", "Then", "Check"
    public bool IsTable { get; set; }
    public bool IsSetVerification { get; set; }
    public string SetVerificationKeyColumns { get; set; } = "";
    public bool IsDecisionTable { get; set; }
    public bool IsAsync { get; set; }

    /// <summary>
    /// The (Task-unwrapped) return type, or "void". When non-void on a Then/Check this
    /// is the actual value for return-value verification.
    /// </summary>
    public string ReturnType { get; set; } = "void";

    public bool HasReturnValue => ReturnType != "void";

    /// <summary>Optional decision-table column name bound to the return value (from [Expected]).</summary>
    public string? ReturnColumn { get; set; }

    /// <summary>Tolerance from a method-level [Approx], or null.</summary>
    public double? ApproxTolerance { get; set; }

    /// <summary>WaitFor timeout in ms (null when the step is not a [WaitFor] step).</summary>
    public int? WaitForTimeoutMs { get; set; }

    /// <summary>WaitFor poll interval in ms (defaults to 100).</summary>
    public int WaitForPollMs { get; set; } = 100;

    public List<ParameterInfo> Parameters { get; set; } = new();
    public CucumberExpressionParser.ParsedExpression? ParsedExpression { get; set; }

    /// <summary>Input (non-out) parameters in declaration order.</summary>
    public List<ParameterInfo> InputParameters
    {
        get
        {
            var list = new List<ParameterInfo>();
            foreach (var p in Parameters) if (!p.IsOut) list.Add(p);
            return list;
        }
    }

    /// <summary>Output (out) parameters in declaration order.</summary>
    public List<ParameterInfo> OutParameters
    {
        get
        {
            var list = new List<ParameterInfo>();
            foreach (var p in Parameters) if (p.IsOut) list.Add(p);
            return list;
        }
    }

    /// <summary>
    /// True when this is a sentence (non-table) step that verifies actual-vs-expected:
    /// it has out parameters and/or a non-void return on a Then.
    /// </summary>
    public bool IsComparisonStep =>
        !IsTable && !IsSetVerification && !IsDecisionTable &&
        (OutParameters.Count > 0 || (HasReturnValue && (StepKind == "Then")));
}

public class ParameterInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsOut { get; set; }
}
