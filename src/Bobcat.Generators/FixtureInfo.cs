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

    /// <summary>Discovered lifecycle hooks, in declaration order.</summary>
    public List<HookMethodInfo> Hooks { get; set; } = new();

    public IEnumerable<HookMethodInfo> HooksOf(HookKind kind)
    {
        foreach (var h in Hooks) if (h.Kind == kind) yield return h;
    }

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

public enum HookKind
{
    /// <summary>Per scenario, inside the scenario's DI scope.</summary>
    BeforeEach,
    AfterEach,

    /// <summary>Once per feature, before any scenario scope exists. Must be static.</summary>
    BeforeAll,
    AfterAll
}

/// <summary>
/// Compile-time model of a discovered lifecycle hook on a fixture.
/// </summary>
public class HookMethodInfo
{
    public string MethodName { get; set; } = "";
    public HookKind Kind { get; set; }
    public bool IsAsync { get; set; }
    public bool IsStatic { get; set; }
    public List<ParameterInfo> Parameters { get; set; } = new();

    public bool IsFeatureLevel => Kind == HookKind.BeforeAll || Kind == HookKind.AfterAll;
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

    /// <summary>The <c>global::</c>-qualified return type — use this when emitting.</summary>
    public string QualifiedReturnType { get; set; } = "void";

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

    /// <summary>[NewScope] — run this step inside a child DI scope.</summary>
    public bool NewScope { get; set; }

    /// <summary>[ScopePerRow] — run each table row inside its own child DI scope.</summary>
    public bool ScopePerRow { get; set; }

    /// <summary>Resource name for the [NewScope]/[ScopePerRow] child scope.</summary>
    public string? ScopeResourceName { get; set; }

    /// <summary>Parameters that come from Gherkin text/table data (not DI).</summary>
    public List<ParameterInfo> ValueParameters
    {
        get
        {
            var list = new List<ParameterInfo>();
            foreach (var p in Parameters) if (!p.IsInjected) list.Add(p);
            return list;
        }
    }

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

    /// <summary>
    /// The readable type name ("int", "string", "MyApp.Customer"). Used for the Gherkin-value
    /// conversion rules, NOT for emission.
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// The <c>global::</c>-qualified type name. Everything emitted INTO generated code as a type
    /// — casts, generic arguments, local declarations, <c>default(...)</c> — must use this: the
    /// generated file lives in the fixture's namespace, where an unqualified name can bind to the
    /// wrong type (e.g. <c>Marten.IDocumentSession</c> resolving to <c>Bobcat.Marten.IDocumentSession</c>
    /// inside <c>Bobcat.Marten.Tests</c>).
    /// </summary>
    public string QualifiedType { get; set; } = "";

    public bool IsOut { get; set; }

    /// <summary>How this parameter is supplied at runtime. <see cref="Binding.Value"/> means
    /// "from a Cucumber capture, a table column, or a DocString".</summary>
    public ParameterBinding Binding { get; set; } = ParameterBinding.Value;

    /// <summary>Resource name for a service resolution (null = the single host resource).</summary>
    public string? ResourceName { get; set; }

    /// <summary>Service key literal for <c>[FromKeyedServices]</c>.</summary>
    public string? ServiceKey { get; set; }

    /// <summary>
    /// True for types a Gherkin cell can be converted into — primitives, string, enums,
    /// DateTime/DateOnly/TimeSpan/Guid/decimal and their nullable forms. Anything else is
    /// treated as a service to resolve rather than a value to parse.
    /// </summary>
    public bool IsSimpleType { get; set; } = true;

    public bool IsInjected => Binding != ParameterBinding.Value;

    /// <summary>
    /// True when an attribute — not the type convention — asked for injection. These win
    /// over a matching data-table header; convention-injected parameters do not.
    /// </summary>
    public bool IsExplicitlyInjected { get; set; }
}

public enum ParameterBinding
{
    /// <summary>Supplied by a Cucumber capture, table column, or DocString.</summary>
    Value,

    /// <summary>The executing step's <c>IStepContext</c>.</summary>
    StepContext,

    /// <summary>Resolved from the per-scenario DI scope.</summary>
    ScopedService,

    /// <summary>A registered <c>ITestResource</c>, looked up on the suite.</summary>
    Resource,

    /// <summary>Resolved from the host's root container.</summary>
    RootService,

    /// <summary>Resolved as a keyed service from the per-scenario DI scope.</summary>
    KeyedService
}
