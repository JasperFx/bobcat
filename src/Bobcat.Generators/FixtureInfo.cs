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

    /// <summary>Grammar modules composed in via [IncludeGrammars].</summary>
    public List<ModuleInfo> Modules { get; set; } = new();

    /// <summary>All step methods available to the feature: the fixture's own plus all modules'.</summary>
    public IEnumerable<StepMethodInfo> AllStepMethods()
    {
        foreach (var m in StepMethods) yield return m;
        foreach (var module in Modules)
            foreach (var m in module.StepMethods)
                yield return m;
    }

    /// <summary>Local variable name used for a module instance within a scenario.</summary>
    public string ModuleLocal(string fullyQualifiedName)
    {
        for (var i = 0; i < Modules.Count; i++)
            if (Modules[i].FullyQualifiedName == fullyQualifiedName)
                return "__m" + i;
        return "__m";
    }

    public ModuleInfo? FindModule(string fullyQualifiedName)
    {
        foreach (var m in Modules)
            if (m.FullyQualifiedName == fullyQualifiedName) return m;
        return null;
    }
}

/// <summary>
/// Compile-time model of a grammar module composed in via [IncludeGrammars].
/// </summary>
public class ModuleInfo
{
    public string FullyQualifiedName { get; set; } = "";
    public bool IsFixture { get; set; }
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

    /// <summary>
    /// Fully-qualified name of the grammar module that declares this step, or null when it
    /// belongs to the fixture itself. Drives call routing in the emitter.
    /// </summary>
    public string? DeclaringModule { get; set; }

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
