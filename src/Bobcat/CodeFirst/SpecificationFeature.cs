using System.Reflection;
using System.Runtime.ExceptionServices;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat.CodeFirst;

/// <summary>
/// Builds the <see cref="FeatureDefinition"/> for a <see cref="Specification"/> type: one scenario
/// per <c>[Scenario]</c> method, lifecycle hooks by the same naming convention a Gherkin fixture
/// uses. This is the one place the code-first API reflects, and it reflects once per type at
/// registration — never per step.
/// </summary>
public static class SpecificationFeature
{
    private static readonly string[] titleSuffixes = ["Specification", "Specs", "Spec", "Fixture"];

    /// <summary>The feature for <typeparamref name="T"/>.</summary>
    public static FeatureDefinition Build<T>() where T : Specification, new() => Build(typeof(T));

    /// <summary>The feature for <paramref name="specificationType"/>.</summary>
    public static FeatureDefinition Build(Type specificationType)
    {
        if (!typeof(Specification).IsAssignableFrom(specificationType))
            throw new BobcatConfigurationException($"{specificationType.FullName} does not inherit {nameof(Specification)}.");

        if (specificationType.IsAbstract)
            throw new BobcatConfigurationException($"{specificationType.FullName} is abstract and cannot be run as a specification.");

        var methods = scenarioMethods(specificationType);
        if (methods.Count == 0)
            throw new BobcatConfigurationException(
                $"{specificationType.FullName} declares no [Scenario] methods. Mark each scenario method with [Scenario].");

        var scenarios = methods.Select(scenario).ToArray();

        if (specificationType.GetConstructor(Type.EmptyTypes) == null)
            throw new BobcatConfigurationException(
                $"{specificationType.FullName} needs a public parameterless constructor — a fresh instance is created for every scenario.");

        return new FeatureDefinition(DeriveTitle(specificationType), specificationType, scenarios)
        {
            BeforeEach = instanceHook(specificationType, "BeforeEach", typeof(BeforeEachAttribute)),
            AfterEach = instanceHook(specificationType, "AfterEach", typeof(AfterEachAttribute)),
            BeforeAll = staticHook(specificationType, "BeforeAll", typeof(BeforeAllAttribute)),
            AfterAll = staticHook(specificationType, "AfterAll", typeof(AfterAllAttribute))
        };
    }

    /// <summary>
    /// The feature title: <c>[FixtureTitle]</c> when present, otherwise the class name with a
    /// <c>Specification</c>/<c>Specs</c>/<c>Spec</c>/<c>Fixture</c> suffix removed and spaces
    /// inserted before capitals — <c>OrderSagaSpecs</c> → "Order Saga".
    /// </summary>
    public static string DeriveTitle(Type specificationType)
    {
        var attribute = specificationType.GetCustomAttribute<FixtureTitleAttribute>(inherit: false);
        if (attribute != null) return attribute.Title;

        var name = specificationType.Name;
        foreach (var suffix in titleSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return Fixture.PascalCaseToTitle(name);
    }

    /// <summary>
    /// The scenario title: the attribute's, or the method name with underscores as spaces
    /// (<c>events_then_response</c> → "events then response"; <c>EventsThenResponse</c> → "Events Then Response").
    /// </summary>
    public static string DeriveScenarioTitle(MethodInfo method)
    {
        var attribute = method.GetCustomAttribute<ScenarioAttribute>();
        if (!string.IsNullOrWhiteSpace(attribute?.Title)) return attribute.Title;

        var name = method.Name;
        return name.Contains('_')
            ? string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries))
            : Fixture.PascalCaseToTitle(name);
    }

    private static List<MethodInfo> scenarioMethods(Type type)
        => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttribute<ScenarioAttribute>() != null)
            // Base-class scenarios first, then declaration order within a type — the order an author
            // reads them in, and therefore the order the report shows them in.
            .OrderBy(m => depth(m.DeclaringType!))
            .ThenBy(m => m.MetadataToken)
            .ToList();

    private static int depth(Type type)
    {
        var depth = 0;
        for (var t = type; t != null; t = t.BaseType) depth++;
        return depth;
    }

    private static ScenarioDefinition scenario(MethodInfo method)
    {
        if (method.ReturnType != typeof(void) || method.GetParameters().Length != 0)
        {
            throw new BobcatConfigurationException(
                $"[Scenario] method {method.DeclaringType!.Name}.{method.Name} must be 'void' with no parameters. A scenario method " +
                "declares steps; it does not run them, so it has nothing to await and nothing to take.");
        }

        var tags = method.GetCustomAttribute<ScenarioAttribute>()!.Tags;
        var title = DeriveScenarioTitle(method);

        return new ScenarioDefinition(title, tags, (fixture, plan) =>
            ((Specification)fixture).Compose(plan, spec => invoke(method, spec, null)));
    }

    // --- hooks ---------------------------------------------------------------------------------

    private static Func<Fixture, IStepContext, Task>? instanceHook(Type type, string name, Type attribute)
    {
        var method = hook(type, name, attribute, isStatic: false);
        if (method == null) return null;

        return async (fixture, context) =>
        {
            fixture.Context = context;
            await invokeAsync(method, fixture, arguments(method, context));
        };
    }

    private static Func<IStepContext, Task>? staticHook(Type type, string name, Type attribute)
    {
        var method = hook(type, name, attribute, isStatic: true);
        if (method == null) return null;

        return context => invokeAsync(method, null, arguments(method, context));
    }

    private static MethodInfo? hook(Type type, string name, Type attribute, bool isStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static | BindingFlags.FlattenHierarchy : BindingFlags.Instance);

        var candidates = type.GetMethods(flags)
            .Where(m => m.GetCustomAttribute(attribute) != null
                        || m.Name.Equals(name, StringComparison.Ordinal)
                        || m.Name.Equals(name + "Async", StringComparison.Ordinal))
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count > 1)
        {
            throw new BobcatConfigurationException(
                $"{type.Name} declares more than one {name} hook ({string.Join(", ", candidates.Select(c => c.Name))}). Keep one.");
        }

        var method = candidates[0];
        var parameters = method.GetParameters();
        var shapeOk = parameters.Length == 0 || (parameters.Length == 1 && parameters[0].ParameterType == typeof(IStepContext));
        var returnOk = method.ReturnType == typeof(void) || method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask);

        if (!shapeOk || !returnOk)
        {
            throw new BobcatConfigurationException(
                $"{type.Name}.{method.Name} must take no parameters or a single IStepContext, and return void, Task or ValueTask.");
        }

        return method;
    }

    private static object?[] arguments(MethodInfo method, IStepContext context)
        => method.GetParameters().Length == 0 ? [] : [context];

    private static void invoke(MethodInfo method, object? target, object?[]? arguments)
    {
        try
        {
            method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static async Task invokeAsync(MethodInfo method, object? target, object?[] arguments)
    {
        object? returned;
        try
        {
            returned = method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            return;
        }

        switch (returned)
        {
            case Task task:
                await task;
                break;
            case ValueTask valueTask:
                await valueTask;
                break;
        }
    }
}
