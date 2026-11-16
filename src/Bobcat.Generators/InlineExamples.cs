using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// <c>[BobcatExamples("""| a | b |…""")]</c> (bobcat#570): a theory's rows as one constant pipe table,
/// read here by the rules <c>Bobcat.SpecExamples</c> reads it by at run time, so a column naming no
/// parameter is a build error (BOBCAT032), a table the runtime would refuse is BOBCAT042, and each row
/// is listed in the spec manifest under the identity it will run as.
/// </summary>
/// <remarks>
/// <para>
/// <b>A row is listed only when its identity is certain</b> — <see cref="SpecRowConstants"/>'s rule.
/// The runtime formats the value <c>CellValues</c> read, so a cell is listed only where the generator
/// can read it the same way: strings and the tokens, enums by member name, numbers, booleans, chars and
/// Guids. A relative time, a date or a domain type is left to run time.
/// </para>
/// <para>
/// <b>A cell is refused only where the runtime would refuse it</b>: a built-in type it cannot parse,
/// an enum value no member names, <c>NULL</c> or <c>EMPTY</c> for a non-nullable value type. A
/// <c>CellValues.Register</c> over a built-in type is not seen here, and is not supported for these.
/// </para>
/// </remarks>
internal static class InlineExamples
{
    internal const string AttributeName = "BobcatExamplesAttribute";

    internal sealed class Problem
    {
        public Problem(bool unknownColumn, string message)
        {
            UnknownColumn = unknownColumn;
            Message = message;
        }

        /// <summary>BOBCAT032 when true (a column names no parameter), else BOBCAT042.</summary>
        public bool UnknownColumn { get; }

        public string Message { get; }
    }

    internal sealed class Reading
    {
        public List<Problem> Problems { get; } = new();

        /// <summary>One row part per data row, or null where the row's identity is not certain.</summary>
        public List<string?> RowParts { get; } = new();
    }

    public static bool IsAttribute(AttributeData attribute) => attribute.AttributeClass?.Name == AttributeName;

    /// <summary>Reads the table on one <c>[BobcatExamples]</c> against the method it decorates.</summary>
    public static Reading Read(AttributeData attribute, IMethodSymbol method)
    {
        var reading = new Reading();
        var where = $"[BobcatExamples] on '{method.Name}'";

        if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string text)
        {
            return reading;
        }

        var rows = TableColumns.Rows(text);
        if (rows.Count == 0)
        {
            reading.Problems.Add(new Problem(false, $"{where} has no header row. Write the parameters' names as the first row: \"| amount | tier |\"."));
            return reading;
        }

        var parameters = method.Parameters;
        var headers = rows[0];
        var columnOf = Enumerable.Repeat(-1, parameters.Length).ToArray();

        for (var column = 0; column < headers.Count; column++)
        {
            var header = headers[column];
            var index = -1;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (string.Equals(ColumnName(parameters[i]), header, StringComparison.OrdinalIgnoreCase)) index = i;
            }

            if (index < 0)
            {
                var names = parameters.Length == 0 ? "none" : string.Join(", ", parameters.Select(ColumnName));
                reading.Problems.Add(new Problem(true, $"The column '{header}' of {where} names no parameter of it (it takes {names})."));
            }
            else if (columnOf[index] >= 0)
            {
                reading.Problems.Add(new Problem(false, $"The column '{header}' of {where} is given twice."));
            }
            else
            {
                columnOf[index] = column;
            }
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            if (columnOf[i] < 0 && !parameters[i].HasExplicitDefaultValue && !parameters[i].IsParams)
            {
                reading.Problems.Add(new Problem(false,
                    $"{where} has no column for the parameter '{ColumnName(parameters[i])}', which has no default value. Add a '{ColumnName(parameters[i])}' column."));
            }
        }

        if (rows.Count == 1)
        {
            reading.Problems.Add(new Problem(false, $"{where} has a header and no rows, so the theory would have no data. Add one row per example."));
        }

        // A params parameter collects however many values a row gives it, which a table cannot say
        var certain = reading.Problems.Count == 0 && !parameters.Any(p => p.IsParams);

        for (var r = 1; r < rows.Count; r++)
        {
            var cells = rows[r];
            if (cells.Count != headers.Count)
            {
                reading.Problems.Add(new Problem(false,
                    $"Row {r} of {where} has {cells.Count} cell(s) where the header has {headers.Count}."));
                reading.RowParts.Add(null);
                continue;
            }

            var formatted = new List<string>(parameters.Length);
            var rowCertain = certain;
            for (var i = 0; i < parameters.Length; i++)
            {
                string? value;
                if (columnOf[i] < 0)
                {
                    value = parameters[i].HasExplicitDefaultValue ? defaultOf(parameters[i]) : null;
                }
                else
                {
                    var cell = cells[columnOf[i]];
                    value = CellReading.Format(cell, parameters[i].Type, out var problem);
                    if (problem != null)
                    {
                        reading.Problems.Add(new Problem(false,
                            $"Row {r} of {where}: the cell '{cell}' in column '{headers[columnOf[i]]}' cannot be read as {parameters[i].Type.ToDisplayString()}: {problem}."));
                    }
                }

                if (value is null) rowCertain = false;
                else formatted.Add(value);
            }

            reading.RowParts.Add(rowCertain
                ? MarkerSpecNaming.RowArguments(parameters.Select(p => p.Name).ToList(), formatted)
                : null);
        }

        return reading;
    }

    /// <summary>The column a parameter answers to: its <c>[Header]</c> title, else its name.</summary>
    public static string ColumnName(IParameterSymbol parameter)
        => parameter.GetAttributes()
               .Where(a => a.AttributeClass?.ToDisplayString() == "Bobcat.HeaderAttribute")
               .Select(a => a.ConstructorArguments.Length > 0 ? a.ConstructorArguments[0].Value as string : null)
               .FirstOrDefault(t => t != null)
           ?? parameter.Name;

    // ---- Diagnostics pass --------------------------------------------------------------------

    public static bool IsCandidate(SyntaxNode node)
        => node is AttributeSyntax attribute
           && lastName(attribute.Name.ToString()) is "BobcatExamples" or AttributeName
           && attribute.Parent?.Parent is MethodDeclarationSyntax;

    public static IReadOnlyList<(Location Location, Problem Problem)>? Check(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var syntax = (AttributeSyntax)context.Node;
        if (syntax.Parent?.Parent is not MethodDeclarationSyntax declaration) return null;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, ct) is not IMethodSymbol method) return null;

        var attribute = method.GetAttributes().FirstOrDefault(a =>
            IsAttribute(a) && a.ApplicationSyntaxReference?.Span == syntax.Span);
        if (attribute is null) return null;

        var problems = Read(attribute, method).Problems;
        if (problems.Count == 0) return null;

        var location = syntax.ArgumentList?.Arguments.FirstOrDefault()?.GetLocation() ?? syntax.GetLocation();
        return problems
            .Select(p => (location, p))
            .ToList();
    }

    private static string lastName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name.Substring(dot + 1);
    }

    // ---- Reading a cell as CellValues.Read will ------------------------------------------------

    private static string? defaultOf(IParameterSymbol parameter)
    {
        var value = parameter.ExplicitDefaultValue;
        if (value is null) return "null";

        var type = CellReading.UnwrapNullable(parameter.Type);
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType) return CellReading.EnumMember(enumType, value);
        return CellReading.Primitive(value);
    }
}
