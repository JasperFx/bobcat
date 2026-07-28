using System.Collections.Generic;

namespace Bobcat.Generators;

/// <summary>
/// Compile-time model of a <c>[TableGrammar]</c> class — a Before-once / per-row / After-once
/// envelope bound to one Gherkin step plus its data table.
/// </summary>
public class TableGrammarInfo
{
    public string FullyQualifiedName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Expression { get; set; } = "";
    public CucumberExpressionParser.ParsedExpression? ParsedExpression { get; set; }

    public GrammarMethodInfo? Before { get; set; }
    public GrammarMethodInfo? Row { get; set; }
    public GrammarMethodInfo? After { get; set; }

    /// <summary>[ScopePerRow] — each row runs in its own child DI scope.</summary>
    public bool ScopePerRow { get; set; }

    /// <summary>Resource name for the [ScopePerRow] child scope.</summary>
    public string? ScopeResourceName { get; set; }

    /// <summary>Class-level [Approx] tolerance applied to expected-column comparisons.</summary>
    public double? ApproxTolerance { get; set; }

    /// <summary>Explicit expected-column name from [Expected] on the Row method.</summary>
    public string? RowExpectedColumn { get; set; }

    /// <summary>
    /// True when a persistence recipe attribute (one deriving from
    /// <c>Bobcat.Runtime.GrammarBehaviorAttribute</c>) is applied. The generator learns nothing
    /// else about it — the behavior is resolved at runtime in the extension package.
    /// </summary>
    public bool HasRecipe { get; set; }

    /// <summary>
    /// The entity type the recipe binds columns to when there is no hand-written <c>Row</c>.
    /// Null means the entity comes from <c>Row</c>'s return value.
    /// </summary>
    public EntityTypeInfo? RecipeEntity { get; set; }
}

/// <summary>
/// Compile-time shape of a recipe's entity type: the public constructors and settable
/// properties columns can bind to. Captured here so the generator can emit a direct
/// <c>new Customer(...)</c> — construction stays compiled code, not a runtime binder.
/// </summary>
public class EntityTypeInfo
{
    public string FullyQualifiedName { get; set; } = "";
    public string Name { get; set; } = "";
    public bool HasParameterlessConstructor { get; set; }
    public List<List<ParameterInfo>> Constructors { get; set; } = new();
    public List<ParameterInfo> SettableProperties { get; set; } = new();
}

/// <summary>
/// Compile-time model of one of a table grammar's three envelope methods.
/// </summary>
public class GrammarMethodInfo
{
    public string MethodName { get; set; } = "";
    public bool IsAsync { get; set; }

    /// <summary>The (Task-unwrapped) return type, or "void".</summary>
    public string ReturnType { get; set; } = "void";

    /// <summary>The <c>global::</c>-qualified return type — use this when emitting.</summary>
    public string QualifiedReturnType { get; set; } = "void";

    public bool HasReturnValue => ReturnType != "void";

    public List<ParameterInfo> Parameters { get; set; } = new();
}
