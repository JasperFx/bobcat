using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// bobcat#623: a step that returns a <c>Task</c>, called as a statement and never awaited (BOBCAT043).
/// </summary>
/// <remarks>
/// <para>
/// One scenario mixes synchronous steps (<c>ThenEvents</c>) with asynchronous ones (<c>ThenDocument</c>,
/// <c>ThenStreamIsStartedWithEvents</c>), and <c>await</c> on a synchronous one is CS4008, so an author cannot
/// simply await everything. A forgotten <c>await</c> is only CS4014 inside an <c>async</c> method, and nothing at
/// all inside a synchronous one. The assertion then runs unobserved, possibly after the scenario closed, and a
/// failure in it never reaches the test: a false green.
/// </para>
/// <para>
/// A step is any method a step attribute (<c>[Given]</c>…<c>[Step]</c>, <c>[BobcatStep]</c>) or an Event Model
/// attribute (<c>[EventModelRole]</c>, <c>[EventModelTrigger]</c>, <c>[EventModelAct]</c>) marks, read from
/// metadata so a vocabulary compiled in another assembly (WolverineFx.Bobcat) is covered. Only an expression
/// statement or a discard (<c>_ = …</c>) counts: a Task that is returned, assigned or passed on is somebody
/// else's to await, and the compiler cannot say whether they do.
/// </para>
/// </remarks>
internal static class UnawaitedSteps
{
    public static bool IsCandidate(SyntaxNode node)
        => node is InvocationExpressionSyntax { Parent: var parent }
           && (parent is ExpressionStatementSyntax || isDiscard(parent));

    /// <summary>The call site to report and what to say about it, or null.</summary>
    public static (Location Location, string Method, string ReturnType)? Check(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node, ct).Symbol is not IMethodSymbol method) return null;
        if (!isAwaitable(method.ReturnType)) return null;
        if (!isStep(method)) return null;

        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var location = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.GetLocation(),
            _ => invocation.Expression.GetLocation()
        };

        return (location, method.Name, method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    private static bool isDiscard(SyntaxNode parent)
        => parent is AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: "_" }, Parent: ExpressionStatementSyntax } assignment
           && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression);

    private static bool isAwaitable(ITypeSymbol type)
        => type is INamedTypeSymbol { Name: "Task" or "ValueTask" } named
           && named.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";

    private static bool isStep(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (StepAttributes.On(definition) is not null) return true;

        return ProjectedRoles.AttributesOf(method, ProjectedRoles.RoleAttributeName).Carrier is not null
               || ProjectedRoles.AttributesOf(method, ProjectedRoles.TriggerAttributeName).Carrier is not null
               || ProjectedRoles.AttributesOf(method, ProjectedRoles.ActAttributeName).Carrier is not null;
    }
}
