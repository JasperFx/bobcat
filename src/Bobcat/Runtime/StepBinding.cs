namespace Bobcat.Runtime;

/// <summary>
/// The binding of one step to the helper method it calls — which method, via which expression.
/// Carried on <see cref="Bobcat.Rendering.PreviewStepRender.Binding"/> so the <c>preview</c> command can show why a step ran the code it ran (issue #208).
/// </summary>
public class StepBinding
{
    public StepBinding(string declaringType, string method, string expression)
    {
        DeclaringType = declaringType;
        Method = method;
        Expression = expression;
    }

    /// <summary>The class declaring the method, fully qualified.</summary>
    public string DeclaringType { get; }

    /// <summary>The method the step text matched.</summary>
    public string Method { get; }

    /// <summary>The step expression the method declares.</summary>
    public string Expression { get; }

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
