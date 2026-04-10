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
    public bool IsAsync { get; set; }
    public List<ParameterInfo> Parameters { get; set; } = new();
    public CucumberExpressionParser.ParsedExpression? ParsedExpression { get; set; }
}

public class ParameterInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}
