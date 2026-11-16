using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Bobcat.Generators;

/// <summary>
/// bobcat#554, the BOBCAT030 replacement: a constant table passed to a step whose body hands it on to
/// <c>TableRunner.Run(this, nameof(row), table)</c> or <c>TableRunner.BuildRows&lt;T&gt;(table)</c> — directly,
/// or through up to <see cref="MaxDepth"/> methods of this compilation that pass the same parameter on — is
/// checked against the row method or <c>T</c> at the step's call site, where the constant is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Columns are BOBCAT032</b>, by <c>TableRunner</c>'s binding: a parameter's <c>[Header]</c> title, else its
/// name, ignoring case; an <c>out</c> parameter's column is an output; a row method returning a value is
/// compared against the one column no parameter names. A column binding to nothing is reported even though
/// the runtime would show it as an unjudged input: it claims something no code reads, and a green run over
/// it is a claim nobody checked. For <c>BuildRows&lt;T&gt;</c> the column rule is the partial object's.
/// </para>
/// <para>
/// <b>Everything else the runtime would refuse is BOBCAT042</b>: a parameter with no column and no default,
/// a row too short to reach a parameter's column, and a cell <see cref="CellReading"/> says
/// <c>CellValues.Read</c> cannot read as its parameter's (or member's) type. Output cells are left to run
/// time, where they are compared through <c>CellCheck</c> and its own expressions and registrations.
/// </para>
/// <para>
/// Anything the generator cannot follow is skipped, the same degradation as a non-constant table: a table
/// put in a variable, rebuilt or filtered; a step compiled into another assembly; a row method named by
/// something other than a constant; a target whose runtime type could carry a different row method.
/// </para>
/// </remarks>
internal static class ForwardedTables
{
    public const int MaxDepth = 3;

    private abstract class Destination
    {
        public abstract string Description { get; }
    }

    private sealed class RowMethod : Destination
    {
        public RowMethod(IMethodSymbol method) => Method = method;
        public IMethodSymbol Method { get; }
        public override string Description => $"{Method.ContainingType.Name}.{Method.Name}";
    }

    private sealed class BuiltType : Destination
    {
        public BuiltType(ITypeSymbol type) => Type = type;
        public ITypeSymbol Type { get; }
        public override string Description => Type.Name;
    }

    private sealed class Memo
    {
        public readonly ConcurrentDictionary<(IMethodSymbol Method, int Ordinal, int Depth), List<Destination>> Forwarded
            = new(new KeyComparer());
    }

    private sealed class KeyComparer : IEqualityComparer<(IMethodSymbol Method, int Ordinal, int Depth)>
    {
        public bool Equals((IMethodSymbol Method, int Ordinal, int Depth) x, (IMethodSymbol Method, int Ordinal, int Depth) y)
            => SymbolEqualityComparer.Default.Equals(x.Method, y.Method) && x.Ordinal == y.Ordinal && x.Depth == y.Depth;

        public int GetHashCode((IMethodSymbol Method, int Ordinal, int Depth) key)
            => (SymbolEqualityComparer.Default.GetHashCode(key.Method) * 31 + key.Ordinal) * 31 + key.Depth;
    }

    private static readonly ConditionalWeakTable<Compilation, Memo> memos = new();

    // ---- Candidates ---------------------------------------------------------------------------

    public static bool IsCandidate(SyntaxNode node)
        => node is InvocationExpressionSyntax { ArgumentList.Arguments.Count: > 0 } invocation
           && invocation.ArgumentList.Arguments.Any(a => mayBeConstantString(a.Expression));

    private static bool mayBeConstantString(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.StringLiteralExpression),
        InterpolatedStringExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax => true,
        BinaryExpressionSyntax binary => binary.IsKind(SyntaxKind.AddExpression),
        _ => false
    };

    public static IReadOnlyList<(Location Location, InlineExamples.Problem Problem)>? Check(
        GeneratorSyntaxContext context, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var model = context.SemanticModel;

        // The cheap question first: is any argument a constant that looks like a table?
        var tables = new List<(ArgumentSyntax Argument, string Text)>();
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (!mayBeConstantString(argument.Expression)) continue;
            if (model.GetConstantValue(argument.Expression, ct) is { HasValue: true, Value: string text }
                && text.IndexOf('|') >= 0)
            {
                tables.Add((argument, text));
            }
        }

        if (tables.Count == 0) return null;
        if (model.GetOperation(invocation, ct) is not IInvocationOperation call) return null;

        var findings = new List<(Location, InlineExamples.Problem)>();
        foreach (var (argumentSyntax, text) in tables)
        {
            var argument = call.Arguments.FirstOrDefault(a => a.Syntax == argumentSyntax);
            if (argument?.Parameter is null || !isStepTable(argument.Parameter.Type)) continue;

            var step = call.TargetMethod;
            var direct = destinationsOf(call, argument.Parameter, model.Compilation, ct);
            var destinations = direct
                               ?? forwarded(step.OriginalDefinition, argument.Parameter.Ordinal, model.Compilation, 0,
                                   new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default), ct);
            var where = direct != null ? "The table" : $"The table passed to {step.Name}";

            foreach (var destination in destinations)
            {
                var problems = destination switch
                {
                    RowMethod row => checkRowMethod(text, row, where),
                    BuiltType built => checkBuiltType(text, built, where, model.Compilation),
                    _ => new List<InlineExamples.Problem>()
                };

                foreach (var problem in problems) findings.Add((argumentSyntax.Expression.GetLocation(), problem));
            }
        }

        return findings.Count == 0 ? null : findings;
    }

    // ---- Following the table ------------------------------------------------------------------

    /// <summary>
    /// Where a call to <c>TableRunner</c> itself sends the table in <paramref name="parameter"/>, or null when
    /// the call is not one of the forms this check reads.
    /// </summary>
    private static List<Destination>? destinationsOf(IInvocationOperation call, IParameterSymbol parameter,
        Compilation compilation, CancellationToken ct)
    {
        var method = call.TargetMethod;
        if (!isTableRunner(method.ContainingType)) return null;

        switch (method.Name)
        {
            case "Run" when method.Parameters.Length == 3
                            && method.Parameters[1].Type.SpecialType == SpecialType.System_String
                            && parameter.Ordinal == 2:
            {
                var name = argumentAt(call, 1)?.ConstantValue;
                if (name is not { HasValue: true, Value: string methodName }) return new List<Destination>();

                var target = argumentAt(call, 0);
                var type = target is null ? null : targetType(target);
                var row = type is null ? null : rowMethodNamed(type, methodName);
                return row is null ? new List<Destination>() : new List<Destination> { new RowMethod(row) };
            }

            case "BuildRows" when method.TypeArguments.Length == 1:
            {
                var type = method.TypeArguments[0];
                return type.TypeKind == TypeKind.TypeParameter || !TableColumns.Checkable(type)
                    ? new List<Destination>()
                    : new List<Destination> { new BuiltType(type) };
            }

            case "Build" when method.Parameters.Length == 3 && parameter.Ordinal == 1:
            {
                var typeOf = argumentAt(call, 0) as ITypeOfOperation;
                return typeOf is null || !TableColumns.Checkable(typeOf.TypeOperand)
                    ? new List<Destination>()
                    : new List<Destination> { new BuiltType(typeOf.TypeOperand) };
            }

            default:
                return new List<Destination>();
        }
    }

    /// <summary>
    /// Every place a method of this compilation hands its <paramref name="ordinal"/>th parameter to
    /// <c>TableRunner</c>, following it through methods that pass it on, <see cref="MaxDepth"/> deep.
    /// </summary>
    private static List<Destination> forwarded(IMethodSymbol method, int ordinal, Compilation compilation, int depth,
        HashSet<IMethodSymbol> visiting, CancellationToken ct)
    {
        if (depth >= MaxDepth || method.DeclaringSyntaxReferences.Length == 0) return new List<Destination>();

        var memo = memos.GetValue(compilation, _ => new Memo());
        var key = (method, ordinal, depth);
        if (memo.Forwarded.TryGetValue(key, out var known)) return known;
        if (!visiting.Add(method)) return new List<Destination>();

        var found = new List<Destination>();
        try
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax(ct);
                var model = compilation.GetSemanticModel(syntax.SyntaxTree);
                var body = syntax switch
                {
                    BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                    LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
                    _ => null
                };
                if (body is null) continue;

                foreach (var node in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetOperation(node, ct) is not IInvocationOperation call) continue;

                    foreach (var argument in call.Arguments)
                    {
                        if (argument.Parameter is null || !isStepTable(argument.Parameter.Type)) continue;
                        if (unwrap(argument.Value) is not IParameterReferenceOperation reference2
                            || reference2.Parameter.Ordinal != ordinal
                            || !SymbolEqualityComparer.Default.Equals(reference2.Parameter.ContainingSymbol, method))
                        {
                            continue;
                        }

                        var direct = destinationsOf(call, argument.Parameter, compilation, ct);
                        found.AddRange(direct ?? forwarded(call.TargetMethod.OriginalDefinition, argument.Parameter.Ordinal,
                            compilation, depth + 1, visiting, ct));
                    }
                }
            }
        }
        finally
        {
            visiting.Remove(method);
        }

        memo.Forwarded[key] = found;
        return found;
    }

    private static IOperation unwrap(IOperation operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation;
    }

    private static IOperation? argumentAt(IInvocationOperation call, int ordinal)
    {
        var argument = call.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == ordinal);
        return argument is null ? null : unwrap(argument.Value);
    }

    /// <summary>
    /// The type <c>TableRunner.MethodNamed</c> will search: the grammar's own type for <c>this</c>, else the
    /// argument's static type only when no subclass could stand in for it at run time.
    /// </summary>
    private static INamedTypeSymbol? targetType(IOperation target)
    {
        if (target is IInstanceReferenceOperation instance) return instance.Type as INamedTypeSymbol;
        return target.Type is INamedTypeSymbol named && (named.IsSealed || named.IsValueType || named.IsRecord) ? named : null;
    }

    /// <summary>
    /// The one method <c>TableRunner.MethodNamed</c> would find: the type's own methods of that name, any
    /// accessibility, and its bases' non-private ones. Null when there is not exactly one — the runtime
    /// reports that itself.
    /// </summary>
    private static IMethodSymbol? rowMethodNamed(INamedTypeSymbol type, string name)
    {
        var candidates = new List<IMethodSymbol>();
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(name).OfType<IMethodSymbol>())
            {
                if (member.MethodKind != MethodKind.Ordinary) continue;
                if (!SymbolEqualityComparer.Default.Equals(current, type) && member.DeclaredAccessibility == Accessibility.Private) continue;
                if (member.IsOverride) continue;
                candidates.Add(member);
            }
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    // ---- Checking ----------------------------------------------------------------------------

    private static List<InlineExamples.Problem> checkRowMethod(string text, RowMethod destination, string where)
    {
        var problems = new List<InlineExamples.Problem>();
        var rows = TableColumns.Rows(text);
        if (rows.Count == 0) return problems;

        var method = destination.Method;
        var target = destination.Description;
        var intro = $"{where} runs its rows through {target}";
        var headers = rows[0];
        var parameters = method.Parameters;
        var names = parameters.Select(InlineExamples.ColumnName).ToList();

        int columnOf(string name)
        {
            for (var c = 0; c < headers.Count; c++)
                if (string.Equals(headers[c], name, StringComparison.OrdinalIgnoreCase)) return c;
            return -1;
        }

        // Columns no parameter claims: one of them is the expected return value when the method returns one
        var leftover = headers
            .Where(h => h.Length > 0 && !names.Any(n => string.Equals(n, h, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var returnsValue = returnsAValue(method);
        if (!(returnsValue && leftover.Count == 1))
        {
            var takes = names.Count == 0 ? "none" : string.Join(", ", names);
            foreach (var column in leftover)
            {
                var why = returnsValue
                    ? $" Its returned value is compared against the one column no parameter names, and this table has {leftover.Count}: {string.Join(", ", leftover)}"
                    : $" It returns nothing to compare the column against";
                problems.Add(new InlineExamples.Problem(true,
                    $"{intro}, and the column '{column}' names no parameter of it (it takes {takes}).{why}."));
            }
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.RefKind == RefKind.Out || parameter.IsParams) continue;

            var column = columnOf(names[i]);
            if (column < 0)
            {
                if (!parameter.HasExplicitDefaultValue)
                {
                    problems.Add(new InlineExamples.Problem(false,
                        $"{intro}, and the table has no '{names[i]}' column for the parameter '{parameter.Name}', which has no default value. Add the column, or give the parameter a default."));
                }

                continue;
            }

            for (var r = 1; r < rows.Count; r++)
            {
                var cells = rows[r];
                if (column >= cells.Count)
                {
                    if (!parameter.HasExplicitDefaultValue)
                    {
                        problems.Add(new InlineExamples.Problem(false,
                            $"{intro}: row {r} has {cells.Count} cell(s) where the header has {headers.Count}, so the parameter '{parameter.Name}' gets no value from column '{headers[column]}'."));
                    }

                    continue;
                }

                CellReading.Format(cells[column], parameter.Type, out var problem);
                if (problem != null)
                {
                    problems.Add(new InlineExamples.Problem(false,
                        $"{intro}: in row {r}, the cell '{cells[column]}' in column '{headers[column]}' cannot be read as {parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}: {problem}."));
                }
            }
        }

        return problems;
    }

    private static List<InlineExamples.Problem> checkBuiltType(string text, BuiltType destination, string where,
        Compilation compilation)
    {
        var problems = new List<InlineExamples.Problem>();
        var rows = TableColumns.Rows(text);
        if (rows.Count == 0) return problems;

        var type = destination.Type;
        var intro = $"{where} builds one {type.Name} per row";
        var headers = rows[0];

        for (var c = 0; c < headers.Count; c++)
        {
            var header = headers[c];
            if (header.Length == 0) continue;

            var problem = TableColumns.Problem(type, header, TableColumns.ColumnRule.Partial, compilation);
            if (problem != null)
            {
                problems.Add(new InlineExamples.Problem(true, $"{intro}, and the column '{header}' does not match {type.Name}: {problem}."));
                continue;
            }

            if (header.IndexOf('.') >= 0) continue;
            var memberType = TableColumns.MemberType(type, header, TableColumns.ColumnRule.Partial);
            if (memberType is null) continue;

            for (var r = 1; r < rows.Count; r++)
            {
                var cells = rows[r];

                // A blank cell is "not specified" when building a partial object
                if (c >= cells.Count || cells[c].Trim().Length == 0) continue;

                CellReading.Format(cells[c], memberType, out var cellProblem);
                if (cellProblem != null)
                {
                    problems.Add(new InlineExamples.Problem(false,
                        $"{intro}: in row {r}, the cell '{cells[c]}' in column '{header}' cannot be read as {memberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}: {cellProblem}."));
                }
            }
        }

        return problems;
    }

    private static bool returnsAValue(IMethodSymbol method)
    {
        if (method.ReturnsVoid) return false;
        var type = method.ReturnType;
        return !(type is INamedTypeSymbol { IsGenericType: false, Name: "Task" or "ValueTask" } named
                 && named.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks");
    }

    private static bool isStepTable(ITypeSymbol type) => type.ToDisplayString().TrimEnd('?') == "Bobcat.StepTable";

    private static bool isTableRunner(INamedTypeSymbol? type)
        => type is { Name: "TableRunner" } && type.ContainingNamespace?.ToDisplayString() == "Bobcat.Runtime";
}
