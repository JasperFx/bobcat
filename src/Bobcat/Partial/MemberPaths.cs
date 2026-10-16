using System.Linq.Expressions;

namespace Bobcat.Partial;

/// <summary>The dotted member path an expression names: <c>x =&gt; x.Address.City</c> is <c>Address.City</c>.</summary>
internal static class MemberPaths
{
    public static string Of(LambdaExpression expression)
        => read(expression.Body) ?? throw new ArgumentException(
            $"'{expression}' is not a member path. Name a property or field of the object, such as x => x.Name or x => x.Address.City.",
            nameof(expression));

    /// <summary>True when <paramref name="body" /> is the parameter followed by one or more members.</summary>
    public static bool IsPath(Expression body) => read(body) != null;

    private static string? read(Expression body)
    {
        var segments = new Stack<string>();
        var current = body;

        // A value-type member read through object, or an implicit conversion, wraps the member access.
        while (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            current = unary.Operand;
        }

        while (current is MemberExpression member)
        {
            segments.Push(member.Member.Name);
            current = member.Expression;
        }

        return current is ParameterExpression && segments.Count > 0 ? string.Join(".", segments) : null;
    }
}
