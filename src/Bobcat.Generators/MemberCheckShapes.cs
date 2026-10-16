using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Bobcat.Generators;

/// <summary>
/// BOBCAT044 (bobcat#624): a member check — a lambda handed to <c>Specify&lt;T&gt;(x =&gt; …)</c> or
/// <c>Specified&lt;T&gt;.Check(x =&gt; …)</c> — that the runtime's <c>MemberChecks.Read</c> would refuse
/// with an <c>ArgumentException</c>, ending the test instead of gathering a wrong. A check is one
/// assertion on one member path, <c>x =&gt; x.Address.City.ShouldBe("Austin")</c>; the shape people
/// reach for and the runtime cannot read is chained Shouldly,
/// <c>x =&gt; x.Attendees.ShouldHaveSingleItem().MemberId.ShouldBe(id)</c>.
/// </summary>
/// <remarks>
/// The rules mirror <c>MemberChecks.Read</c> and <c>MemberPaths.Of</c> exactly — the body is a call, the
/// assertion's subject (an extension's first argument, else the receiver) is the parameter followed by
/// one or more properties or fields, and no other argument reads the parameter — so the build refuses
/// exactly what the run would have. Conversions are looked through, as the runtime looks through
/// <c>Convert</c>. A method is a member-check sink when it is Bobcat's own (<c>Specifications.Specify</c>,
/// <c>CritterStackSpec.Specify</c>, <c>Specified&lt;T&gt;.Check</c>) or a wrapper of the same shape: named
/// <c>Specify</c> or <c>Check</c>, taking <c>Expression&lt;Action&lt;T&gt;&gt;</c>, returning <c>Specified&lt;T&gt;</c>.
/// </remarks>
internal static class MemberCheckShapes
{
    public static bool IsCandidate(SyntaxNode node)
        => node is InvocationExpressionSyntax invocation
           && invocation.ArgumentList.Arguments.Any(x => x.Expression is LambdaExpressionSyntax)
           && nameOf(invocation.Expression) is "Specify" or "Check";

    public static IReadOnlyList<(Location Location, string Message)>? Check(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var model = context.SemanticModel;
        if (model.GetOperation(invocation, ct) is not IInvocationOperation call) return null;
        if (!isSink(call.TargetMethod)) return null;

        var findings = new List<(Location, string)>();
        foreach (var argument in call.Arguments)
        {
            // params arrives as an implicit array of the lambdas; a single parameter as the lambda itself
            var lambdas = argument.Value is IArrayCreationOperation { Initializer: { } initializer }
                ? initializer.ElementValues
                : (IEnumerable<IOperation>)new[] { argument.Value };

            foreach (var value in lambdas)
            {
                if (unwrap(value) is not IAnonymousFunctionOperation lambda) continue;
                if (lambda.Syntax is not LambdaExpressionSyntax { ExpressionBody: { } body } syntax) continue;
                if (!isCheckType(value.Type)) continue;

                var problem = problemWith(lambda.Symbol.Parameters.FirstOrDefault(), model.GetOperation(body, ct), body);
                if (problem != null) findings.Add((syntax.GetLocation(), $"'{syntax}' {problem}"));
            }
        }

        return findings.Count == 0 ? null : findings;
    }

    private static string? problemWith(IParameterSymbol? parameter, IOperation? body, ExpressionSyntax syntax)
    {
        if (parameter == null || body == null) return null;

        if (unwrap(body) is not IInvocationOperation assertion)
            return "is not a check on a member. Write it as one assertion on one member, such as x => x.Age.ShouldBe(52).";

        // An extension (Shouldly's) or a static assertion takes the member as its first argument; an
        // instance method is called on it — MemberChecks.Read's reading
        var ordered = assertion.Arguments.OrderBy(x => x.Parameter?.Ordinal ?? int.MaxValue).ToList();
        var subject = assertion.Instance ?? ordered.FirstOrDefault()?.Value;
        if (subject == null)
            return "is not a check on a member. Write it as one assertion on one member, such as x => x.Age.ShouldBe(52).";

        if (!isMemberPath(subject, parameter))
        {
            return $"asserts on '{subject.Syntax}', which is not a member of the object. A member check is one assertion on one "
                   + "property or field path, such as x => x.Address.City.ShouldBe(\"Austin\"); a chained assertion "
                   + "(x => x.Lines.ShouldHaveSingleItem().Sku.ShouldBe(\"A\")) cannot be read as one. To claim a collection "
                   + "member's items, specify them: .With(x => x.Lines, Expect.Exactly(Specify<Line>().With(l => l.Sku, \"A\"))).";
        }

        var compared = assertion.Instance == null ? ordered.Skip(1) : ordered;
        if (compared.Any(x => reads(x.Value, parameter)))
            return "compares the member with another member of the same object. A check compares one member with a value.";

        return null;
    }

    private static bool isMemberPath(IOperation subject, IParameterSymbol parameter)
    {
        var current = unwrap(subject);
        var segments = 0;
        while (true)
        {
            switch (current)
            {
                case IPropertyReferenceOperation { Arguments.Length: 0 } property:
                    current = property.Instance == null ? null : unwrap(property.Instance);
                    segments++;
                    continue;
                case IFieldReferenceOperation field:
                    current = field.Instance == null ? null : unwrap(field.Instance);
                    segments++;
                    continue;
                case IParameterReferenceOperation reference:
                    return segments > 0 && SymbolEqualityComparer.Default.Equals(reference.Parameter, parameter);
                default:
                    return false;
            }
        }
    }

    private static bool reads(IOperation operation, IParameterSymbol parameter)
        => operation.DescendantsAndSelf().OfType<IParameterReferenceOperation>()
            .Any(x => SymbolEqualityComparer.Default.Equals(x.Parameter, parameter));

    private static IOperation unwrap(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IDelegateCreationOperation creation:
                    operation = creation.Target;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                default:
                    return operation;
            }
        }
    }

    private static bool isSink(IMethodSymbol method)
    {
        if (method.Name is not ("Specify" or "Check")) return false;
        var ns = method.ContainingType?.ContainingNamespace?.ToDisplayString();
        if (ns == "Bobcat" || ns?.StartsWith("Bobcat.") == true) return true;

        // A grammar's own wrapper of the same shape
        return method.ReturnType is INamedTypeSymbol { Name: "Specified", Arity: 1 } returned
               && returned.ContainingNamespace?.ToDisplayString() == "Bobcat";
    }

    // Expression<Action<T>>: the type a member check's lambda converts to
    private static bool isCheckType(ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: "Expression", Arity: 1 } expression
           && expression.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions"
           && expression.TypeArguments[0] is INamedTypeSymbol { Name: "Action", Arity: 1 } action
           && action.ContainingNamespace?.ToDisplayString() == "System";

    private static string? nameOf(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null
    };
}
