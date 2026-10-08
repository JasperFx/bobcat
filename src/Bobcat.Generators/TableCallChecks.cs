using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// BOBCAT032 over C# call sites (bobcat#415, bobcat#420): a COMPILE-TIME CONSTANT table — a raw
/// string, or a constant interpolated string built from <c>nameof</c> — whose columns do not name
/// members of the type it is about. Four shapes:
/// <list type="bullet">
/// <item>a property check, <c>(object subject, StepTable expected)</c> — <c>PropertyCells.Verify</c>,
/// <c>Fixture.VerifyObject</c>, and any method with that parameter pair (Wolverine's
/// <c>Verify(state, table)</c>), checked against the subject's static type;</item>
/// <item>a set verification, <c>(IEnumerable actual, StepTable expected)</c>, checked against the
/// element type, with a constant <c>keyColumns</c> too;</item>
/// <item><c>PartialObjects.FromTable(typeof(T), table)</c>;</item>
/// <item><c>Specified&lt;T&gt;.With("path", value)</c> with a constant path.</item>
/// </list>
/// </summary>
/// <remarks>
/// A property check and a set verification read the RUNTIME type, so a subject typed as an open
/// class is skipped — a subclass may carry the column (#415's option 1: error only for a struct, a
/// sealed class, a record, or a class nothing derives from). A partial object builds exactly
/// <c>T</c>, so it is always checked.
/// </remarks>
internal static class TableCallChecks
{
    private static readonly HashSet<string> MethodNames = new(StringComparer.Ordinal)
    {
        "Verify", "VerifyObject", "VerifySet", "FromTable", "With"
    };

    public sealed class Finding
    {
        public Finding(Location location, string message)
        {
            Location = location;
            Message = message;
        }

        public Location Location { get; }
        public string Message { get; }
    }

    public static bool IsCandidate(SyntaxNode node)
        => node is InvocationExpressionSyntax invocation && MethodNames.Contains(nameOf(invocation.Expression) ?? "");

    public static IReadOnlyList<Finding>? Extract(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var model = context.SemanticModel;
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return null;

        var findings = new List<Finding>();

        if (method.Name == "With" && isBobcatType(method.ContainingType, "Specified"))
        {
            checkSpecifiedPath(invocation, method, model, findings, ct);
        }
        else if (method.Name == "FromTable" && isBobcatType(method.ContainingType, "PartialObjects"))
        {
            checkFromTable(invocation, method, model, findings, ct);
        }
        else
        {
            checkTableShape(invocation, method, model, findings, ct);
        }

        return findings.Count == 0 ? null : findings;
    }

    private static void checkTableShape(InvocationExpressionSyntax invocation, IMethodSymbol method,
        SemanticModel model, List<Finding> findings, CancellationToken ct)
    {
        var parameters = method.Parameters;
        for (var k = 1; k < parameters.Length; k++)
        {
            if (!isStepTable(parameters[k].Type)) continue;

            var subjectParameter = parameters[k - 1];
            var tableArgument = argumentFor(invocation, method, k);
            var subjectArgument = argumentFor(invocation, method, k - 1);
            if (tableArgument is null || subjectArgument is null) return;

            var text = constantString(tableArgument.Expression, model, ct);
            if (text is null) return;

            var subjectType = model.GetTypeInfo(subjectArgument.Expression, ct).Type;
            if (subjectType is null) return;

            if (subjectParameter.Type.SpecialType == SpecialType.System_Object)
            {
                checkColumns(text, subjectType, TableColumns.ColumnRule.Property, requireClosed: true,
                    tableArgument.Expression.GetLocation(), model.Compilation, findings);
                return;
            }

            if (isEnumerable(subjectParameter.Type))
            {
                var element = elementType(subjectType);
                if (element is null || TableColumns.IsScalar(element)) return;

                checkColumns(text, element, TableColumns.ColumnRule.Set, requireClosed: true,
                    tableArgument.Expression.GetLocation(), model.Compilation, findings);

                var keyIndex = indexOf(parameters, "keyColumns");
                if (keyIndex >= 0 && argumentFor(invocation, method, keyIndex) is { } keyArgument
                                  && constantString(keyArgument.Expression, model, ct) is { } keys
                                  && TableColumns.Checkable(element)
                                  && TableColumns.IsClosed(element, model.Compilation))
                {
                    foreach (var key in keys.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                    {
                        var problem = TableColumns.Problem(element, key, TableColumns.ColumnRule.Set, model.Compilation);
                        if (problem != null)
                            findings.Add(new Finding(keyArgument.Expression.GetLocation(),
                                $"The key column '{key}' does not match {element.Name}: {problem}"));
                    }
                }
            }

            return;
        }
    }

    private static void checkFromTable(InvocationExpressionSyntax invocation, IMethodSymbol method,
        SemanticModel model, List<Finding> findings, CancellationToken ct)
    {
        var typeArgument = argumentFor(invocation, method, 0);
        var tableArgument = argumentFor(invocation, method, 1);
        if (typeArgument?.Expression is not TypeOfExpressionSyntax typeOf || tableArgument is null) return;

        var type = model.GetTypeInfo(typeOf.Type, ct).Type;
        var text = constantString(tableArgument.Expression, model, ct);
        if (type is null || text is null) return;

        checkColumns(text, type, TableColumns.ColumnRule.Partial, requireClosed: false,
            tableArgument.Expression.GetLocation(), model.Compilation, findings);
    }

    private static void checkSpecifiedPath(InvocationExpressionSyntax invocation, IMethodSymbol method,
        SemanticModel model, List<Finding> findings, CancellationToken ct)
    {
        if (method.Parameters.Length == 0 || method.Parameters[0].Type.SpecialType != SpecialType.System_String) return;
        if (method.ContainingType.TypeArguments.FirstOrDefault() is not { } type) return;
        if (!TableColumns.Checkable(type)) return;

        var pathArgument = argumentFor(invocation, method, 0);
        if (pathArgument is null || constantString(pathArgument.Expression, model, ct) is not { } path) return;

        var problem = TableColumns.Problem(type, path, TableColumns.ColumnRule.Partial, model.Compilation);
        if (problem != null)
            findings.Add(new Finding(pathArgument.Expression.GetLocation(),
                $"The path '{path}' does not match {type.Name}: {problem}. Check the spelling, or the member may have been renamed since this spec was written"));
    }

    private static void checkColumns(string text, ITypeSymbol type, TableColumns.ColumnRule rule, bool requireClosed,
        Location location, Compilation compilation, List<Finding> findings)
    {
        if (!TableColumns.Checkable(type)) return;
        if (requireClosed && !TableColumns.IsClosed(type, compilation)) return;

        var columns = TableColumns.ColumnsOf(text);
        if (columns is null) return;

        foreach (var column in columns)
        {
            var problem = TableColumns.Problem(type, column, rule, compilation);
            if (problem != null)
                findings.Add(new Finding(location,
                    $"The table column '{column}' does not match {type.Name}: {problem}. Check the spelling, or the member may have been renamed since this spec was written"));
        }
    }

    /// <summary>
    /// The value of a compile-time constant string — a literal, a raw string, or an interpolated
    /// string whose holes are all constants such as <c>nameof(Foo.Bar)</c> — or null.
    /// </summary>
    internal static string? constantString(ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    {
        var constant = model.GetConstantValue(expression, ct);
        return constant.HasValue ? constant.Value as string : null;
    }

    private static ArgumentSyntax? argumentFor(InvocationExpressionSyntax invocation, IMethodSymbol method, int index)
    {
        var parameter = method.Parameters[index];
        var arguments = invocation.ArgumentList.Arguments;

        foreach (var argument in arguments)
        {
            if (argument.NameColon?.Name.Identifier.ValueText == parameter.Name) return argument;
        }

        if (index < arguments.Count && arguments[index].NameColon is null) return arguments[index];
        return null;
    }

    private static int indexOf(IEnumerable<IParameterSymbol> parameters, string name)
    {
        var i = 0;
        foreach (var parameter in parameters)
        {
            if (parameter.Name == name) return i;
            i++;
        }

        return -1;
    }

    private static bool isStepTable(ITypeSymbol type) => type.ToDisplayString().TrimEnd('?') == "Bobcat.StepTable";

    private static bool isBobcatType(INamedTypeSymbol? type, string name)
        => type != null && type.Name == name && type.ContainingNamespace?.ToDisplayString() == "Bobcat";

    private static bool isEnumerable(ITypeSymbol type)
        => type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
           || type.SpecialType == SpecialType.System_Collections_IEnumerable;

    private static ITypeSymbol? elementType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;
        if (type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            return named.TypeArguments[0];

        return type.AllInterfaces
            .FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            ?.TypeArguments[0];
    }

    private static string? nameOf(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null
    };
}
