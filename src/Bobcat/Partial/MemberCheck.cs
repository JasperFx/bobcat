using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Bobcat.Engine;
using Bobcat.Partial;

namespace Bobcat;

/// <summary>
/// A check on one member that is not plain equality, written as an assertion on the member:
/// <c>x =&gt; x.Age.ShouldBeGreaterThan(3)</c> (bobcat#450). It is a specified member like any other, so it
/// shows in the same member-by-member table, described as the assertion reads: <c>should be greater than 3</c>.
/// </summary>
/// <remarks>
/// <c>x =&gt; x.Age.ShouldBe(52)</c> never becomes one: equality is an ordinary specified value. Any assertion
/// library works, because the check runs the assertion against the member's actual value and treats
/// an exception as the disagreement.
/// </remarks>
public sealed class MemberCheck
{
    private readonly Delegate _assertion;

    internal MemberCheck(string description, Delegate assertion)
    {
        Description = description;
        _assertion = assertion;
    }

    /// <summary>The assertion as it reads: <c>should be greater than 3</c>.</summary>
    public string Description { get; }

    /// <summary>Run the assertion on <paramref name="actual" />: null when it holds, else why it does not.</summary>
    public string? Run(object? actual)
    {
        try
        {
            _assertion.DynamicInvoke(actual);
            return null;
        }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
        {
            return firstLine(e.InnerException.Message);
        }
        catch (ArgumentException e)
        {
            return $"{Description}, but the value was not of the member's type: {firstLine(e.Message)}";
        }
    }

    public override string ToString() => Description;

    private static string firstLine(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length == 0 ? "the check failed" : line;
    }
}

/// <summary>Reads <c>x =&gt; x.Member.Assertion(args)</c> into the member's path and what it expects.</summary>
internal static class MemberChecks
{
    /// <summary>The member path, and either the expected value (plain <c>ShouldBe</c>) or a <see cref="MemberCheck" />.</summary>
    public static (string Path, object? Expected) Read<T>(Expression<Action<T>> check)
    {
        var body = check.Body;
        if (body is not MethodCallExpression call)
            throw new ArgumentException(
                $"'{check}' is not a check on a member. Write it as an assertion on one member, such as x => x.Age.ShouldBe(52).",
                nameof(check));

        // An extension assertion (Shouldly's) takes the member as its first argument; an instance
        // method is called on it
        var (subject, arguments) = call.Object is null && call.Arguments.Count > 0
            ? (call.Arguments[0], call.Arguments.Skip(1).ToList())
            : (call.Object!, call.Arguments.ToList());

        var path = MemberPaths.Of(Expression.Lambda(subject, check.Parameters));
        var parameters = call.Method.GetParameters().Skip(call.Object is null ? 1 : 0).ToList();

        // What the assertion compares against: every argument except a custom failure message
        var compared = arguments
            .Select((argument, i) => (argument, parameter: i < parameters.Count ? parameters[i] : null))
            .Where(x => !isMessage(x.parameter))
            .Select(x => x.argument)
            .ToList();

        if (call.Method.Name == "ShouldBe" && compared.Count == 1)
        {
            return (path, evaluate(compared[0], check));
        }

        var values = compared.Select(x => evaluate(x, check)).ToList();
        var description = describe(call.Method.Name, values);

        // The same call, on the member's value rather than on the object
        var value = Expression.Parameter(subject.Type, "value");
        var onValue = call.Object is null
            ? Expression.Call(call.Method, new[] { (Expression)value }.Concat(call.Arguments.Skip(1)))
            : Expression.Call(value, call.Method, call.Arguments);
        var assertion = Expression.Lambda(onValue, value).Compile(preferInterpretation: true);

        return (path, new MemberCheck(description, assertion));
    }

    private static bool isMessage(System.Reflection.ParameterInfo? parameter)
        => parameter is not null
           && (parameter.Name is "customMessage" or "message"
               || parameter.ParameterType == typeof(Func<string?>)
               || parameter.ParameterType == typeof(Func<string>));

    // An argument must not read the object under test: it is what the member is compared against
    private static object? evaluate(Expression argument, LambdaExpression check)
    {
        if (new ParameterFinder(check.Parameters[0]).Finds(argument))
            throw new ArgumentException(
                $"'{check}' compares the member with another member of the same object. A check compares one member with a value.",
                nameof(check));

        return Expression.Lambda(argument).Compile(preferInterpretation: true).DynamicInvoke();
    }

    // ShouldBeGreaterThan(3) -> "should be greater than 3"
    private static string describe(string method, IReadOnlyList<object?> values)
    {
        var words = Regex.Replace(method, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        return values.Count == 0 ? words : $"{words} {string.Join(", ", values.Select(ScenarioValues.Format))}";
    }

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        private bool _found;

        public bool Finds(Expression expression)
        {
            Visit(expression);
            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter) _found = true;
            return node;
        }
    }
}
