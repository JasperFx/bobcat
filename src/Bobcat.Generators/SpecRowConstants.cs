using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// The constant rows of a data-driven test, read at compile time so each one can be listed in the
/// spec manifest under its own identity (bobcat#569).
/// </summary>
/// <remarks>
/// <para>
/// <b>Constant rows only.</b> <c>[InlineData]</c> and <c>[BobcatExample]</c> (xUnit),
/// <c>[Arguments]</c> (TUnit), <c>[TestCase]</c> (NUnit) and <c>[DataRow]</c> (MSTest) carry their
/// values in the attribute, so the row identity the runtime will publish can be computed here.
/// <c>[MemberData]</c>, <c>[ClassData]</c> and their kin are values only a run produces: those rows
/// get identities at run time and are not listed ahead of it.
/// </para>
/// <para>
/// <b>A row is listed only when its identity is certain.</b> The runtime formats the value the
/// runner actually passes, after any conversion to the parameter's type, so a constant is listed only
/// when that conversion cannot change its text: the same type as the parameter, an integral number
/// for an integral parameter, or a parameter typed <c>object</c>. Anything else (a string a runner
/// converts to a <c>Guid</c>, an array, a <c>params</c> parameter, an enum value no member names) is
/// left to run time rather than listed under an identity it might not have.
/// </para>
/// <para>
/// Attributes are matched by name, never by reference, like every other runner attribute here: the
/// generator references no test framework.
/// </para>
/// </remarks>
internal static class SpecRowConstants
{
    internal sealed class Row
    {
        public Row(string part, Location? where)
        {
            Part = part;
            Where = where;
        }

        /// <summary><c>(amount: 100, tier: Gold)</c> or <c>[label]</c>.</summary>
        public string Part { get; }

        /// <summary>The attribute that declares the row, where BOBCAT036 points.</summary>
        public Location? Where { get; }
    }

    private static readonly HashSet<string> rowAttributes = new(StringComparer.Ordinal)
    {
        "InlineDataAttribute", "BobcatExampleAttribute", "ArgumentsAttribute", "TestCaseAttribute", "DataRowAttribute"
    };

    /// <summary>The constant rows of <paramref name="method"/> whose identities are certain, in source order.</summary>
    public static IReadOnlyList<Row> Read(MethodDeclarationSyntax method, SemanticModel model, CancellationToken ct)
    {
        if (method.ParameterList.Parameters.Count == 0) return Array.Empty<Row>();
        if (model.GetDeclaredSymbol(method, ct) is not IMethodSymbol symbol) return Array.Empty<Row>();

        var parameters = symbol.Parameters;

        // A params parameter collects however many values a row gives it; which ones reach it, and as
        // what, is the runner's business.
        if (parameters.Any(x => x.IsParams)) return Array.Empty<Row>();

        var names = parameters.Select(x => x.Name).ToList();
        var rows = new List<Row>();

        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.Name is not { } name || !rowAttributes.Contains(name)) continue;
            if (attribute.AttributeConstructor is null) continue;

            var where = attribute.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation();

            if (rowOf(attribute, name, parameters, names) is { } part) rows.Add(new Row(part, where));
        }

        return rows;
    }

    private static string? rowOf(
        AttributeData attribute, string name, IReadOnlyList<IParameterSymbol> parameters, IReadOnlyList<string> names)
    {
        var values = new List<TypedConstant>();
        string? label = null;

        var constructorParameters = attribute.AttributeConstructor!.Parameters;
        for (var i = 0; i < attribute.ConstructorArguments.Length; i++)
        {
            var argument = attribute.ConstructorArguments[i];

            // [BobcatExample("label", …)]: the label is the first argument, not a value.
            if (i == 0 && name == "BobcatExampleAttribute")
            {
                label = argument.Value as string;
                continue;
            }

            if (i < constructorParameters.Length && constructorParameters[i].IsParams)
            {
                // A params array given a single null is one null value, which is what the runners do.
                if (argument.IsNull) values.Add(argument);
                else if (argument.Kind == TypedConstantKind.Array) values.AddRange(argument.Values);
                else return null;
            }
            else
            {
                values.Add(argument);
            }
        }

        // xUnit's own row label (DataAttribute.Label). Only xUnit carries one to the test at run time.
        if (name is "InlineDataAttribute" or "BobcatExampleAttribute")
        {
            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "Label" && named.Value.Value is string text) label = text;
            }
        }

        if (label is { } given && given.Trim().Length > 0) return MarkerSpecNaming.RowLabel(given);

        if (values.Count != parameters.Count) return null;

        var formatted = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            if (format(values[i], parameters[i].Type) is not { } text) return null;
            formatted.Add(text);
        }

        return MarkerSpecNaming.RowArguments(names, formatted);
    }

    /// <summary>
    /// A constant as the runtime's <c>SpecRows.Format</c> will render the value the runner passes, or
    /// null when that is not certain.
    /// </summary>
    private static string? format(TypedConstant constant, ITypeSymbol parameterType)
    {
        if (constant.IsNull) return "null";

        var target = unwrapNullable(parameterType);

        switch (constant.Kind)
        {
            case TypedConstantKind.Enum:
                if (!sameType(constant.Type, target)) return null;
                return constant.Type is INamedTypeSymbol enumType ? enumMember(enumType, constant.Value) : null;

            case TypedConstantKind.Type:
                if (!sameType(constant.Type, target)) return null;
                return constant.Value is INamedTypeSymbol { IsGenericType: false } type ? type.Name : null;

            case TypedConstantKind.Primitive:
                if (!sameType(constant.Type, target) && !(integral(constant.Type) && integral(target))) return null;
                return primitive(constant.Value);

            default:
                return null;
        }
    }

    private static string? primitive(object? value) => value switch
    {
        null => "null",
        string text => MarkerSpecNaming.EscapeRowValue(text),
        char c => MarkerSpecNaming.EscapeRowValue(c.ToString()),
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => null
    };

    private static string? enumMember(INamedTypeSymbol enumType, object? value)
        => enumType.GetMembers().OfType<IFieldSymbol>()
            .FirstOrDefault(field => field.HasConstantValue && Equals(field.ConstantValue, value))?.Name;

    /// <summary>
    /// The constant reaches the method as itself: the same type as the parameter, or a parameter typed
    /// <c>object</c>, which the runner hands the value unconverted.
    /// </summary>
    private static bool sameType(ITypeSymbol? constantType, ITypeSymbol target)
        => target.SpecialType == SpecialType.System_Object
           || SymbolEqualityComparer.Default.Equals(constantType, target);

    private static bool integral(ITypeSymbol? type) => type?.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16
        or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

    private static ITypeSymbol unwrapNullable(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;
}
