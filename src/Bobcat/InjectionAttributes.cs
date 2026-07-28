namespace Bobcat;

/// <summary>
/// Forces a step/grammar parameter to be resolved from the CURRENT SCENARIO's DI scope,
/// even when the parameter name also matches a data-table header. The default binder rule
/// already prefers a column when the name matches, so this is the explicit override.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class FromScopedServiceAttribute : Attribute
{
    /// <summary>Optional resource name, when more than one host resource is registered.</summary>
    public string? Resource { get; set; }

    public FromScopedServiceAttribute() { }
    public FromScopedServiceAttribute(string resource) => Resource = resource;
}

/// <summary>
/// Forces a step/grammar parameter to be resolved from the host's ROOT container, bypassing
/// the per-scenario scope. Use for singletons and for hooks that run outside a scenario.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class FromRootServiceAttribute : Attribute
{
    /// <summary>Optional resource name, when more than one host resource is registered.</summary>
    public string? Resource { get; set; }

    public FromRootServiceAttribute() { }
    public FromRootServiceAttribute(string resource) => Resource = resource;
}

/// <summary>
/// Resolves a keyed service from the current scenario's DI scope.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class FromKeyedServicesAttribute : Attribute
{
    public object? Key { get; }

    /// <summary>Optional resource name, when more than one host resource is registered.</summary>
    public string? Resource { get; set; }

    public FromKeyedServicesAttribute(string key) => Key = key;
}

/// <summary>
/// Runs the decorated step or grammar inside a CHILD DI scope nested under the scenario
/// scope. Injected services resolve from the child scope and are disposed when the step
/// finishes — the host resource still owns the scenario scope.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class NewScopeAttribute : Attribute
{
    /// <summary>Optional resource name, when more than one host resource is registered.</summary>
    public string? Resource { get; set; }
}

/// <summary>
/// For table-driven steps: runs EACH ROW in its own child DI scope nested under the
/// scenario scope. The isolation hatch for decision tables whose rows must not share
/// scoped service instances.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class ScopePerRowAttribute : Attribute
{
    /// <summary>Optional resource name, when more than one host resource is registered.</summary>
    public string? Resource { get; set; }
}
