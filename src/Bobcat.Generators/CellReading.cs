using System;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Bobcat.Generators;

/// <summary>
/// Reading a constant table cell the way <c>CellValues.Read</c> will at run time, shared by every
/// compile-time cell check: <c>[BobcatExamples]</c> (bobcat#570) and a constant table a step hands to
/// <c>TableRunner</c> (bobcat#554), both BOBCAT042.
/// </summary>
/// <remarks>
/// <b>A cell is refused only where the runtime would certainly refuse it</b>: a built-in type it cannot
/// parse, an enum value no member names, <c>NULL</c> or <c>EMPTY</c> for a non-nullable value type. Dates,
/// times, relative times and domain types are left to run time. A <c>CellValues.Register</c> over a built-in
/// type is not seen here (deferred, bobcat#554).
/// </remarks>
internal static class CellReading
{
    /// <summary>
    /// The cell as <c>SpecRows.Format(CellValues.Read(cell, type))</c> will render it, or null when that
    /// is not certain. <paramref name="problem"/> is set when the runtime would refuse the cell.
    /// </summary>
    public static string? Format(string cell, ITypeSymbol parameterType, out string? problem)
    {
        problem = null;

        var target = UnwrapNullable(parameterType);
        var nullable = !SymbolEqualityComparer.Default.Equals(target, parameterType) || !target.IsValueType;
        var isString = target.SpecialType == SpecialType.System_String;

        var trimmed = cell.Trim();

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
        {
            var literal = trimmed.Substring(1, trimmed.Length - 2);
            return isString ? MarkerSpecNaming.EscapeRowValue(literal) : convert(literal, target, out problem);
        }

        if (string.Equals(trimmed, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            if (nullable) return "null";
            problem = $"{target.Name} cannot be null";
            return null;
        }

        if (string.Equals(trimmed, "EMPTY", StringComparison.OrdinalIgnoreCase))
        {
            if (isString) return "";
            if (nullable) return "null";
            problem = $"{target.Name} has no empty value";
            return null;
        }

        if (trimmed.Length == 0)
        {
            if (isString) return "";
            if (nullable) return "null";
        }

        // A relative time against a date type, like every date, is left to the runtime (convert's default)
        return convert(trimmed, target, out problem);
    }

    private static string? convert(string raw, ITypeSymbol target, out string? problem)
    {
        problem = null;
        var invariant = CultureInfo.InvariantCulture;

        if (target.TypeKind == TypeKind.Enum && target is INamedTypeSymbol enumType)
        {
            var members = enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
            var named = members.FirstOrDefault(f => string.Equals(f.Name, raw, StringComparison.OrdinalIgnoreCase));
            if (named != null) return EnumMember(enumType, named.ConstantValue);

            // A number or a flags combination is something Enum.TryParse accepts; what it reads as is the runtime's.
            if (raw.Length > 0 && (char.IsDigit(raw[0]) || raw[0] == '-' || raw.IndexOf(',') >= 0)) return null;

            problem = $"'{raw}' is not one of {enumType.Name}'s values ({string.Join(", ", members.Select(m => m.Name))})";
            return null;
        }

        switch (target.SpecialType)
        {
            case SpecialType.System_String:
                return MarkerSpecNaming.EscapeRowValue(raw);
            case SpecialType.System_Boolean:
                if (bool.TryParse(raw, out var b)) return b ? "true" : "false";
                break;
            case SpecialType.System_Char:
                if (raw.Length == 1) return MarkerSpecNaming.EscapeRowValue(raw);
                break;
            case SpecialType.System_SByte:
                if (sbyte.TryParse(raw, NumberStyles.Integer, invariant, out var sb)) return sb.ToString(invariant);
                break;
            case SpecialType.System_Byte:
                if (byte.TryParse(raw, NumberStyles.Integer, invariant, out var by)) return by.ToString(invariant);
                break;
            case SpecialType.System_Int16:
                if (short.TryParse(raw, NumberStyles.Integer, invariant, out var s)) return s.ToString(invariant);
                break;
            case SpecialType.System_UInt16:
                if (ushort.TryParse(raw, NumberStyles.Integer, invariant, out var us)) return us.ToString(invariant);
                break;
            case SpecialType.System_Int32:
                if (int.TryParse(raw, NumberStyles.Integer, invariant, out var i)) return i.ToString(invariant);
                break;
            case SpecialType.System_UInt32:
                if (uint.TryParse(raw, NumberStyles.Integer, invariant, out var ui)) return ui.ToString(invariant);
                break;
            case SpecialType.System_Int64:
                if (long.TryParse(raw, NumberStyles.Integer, invariant, out var l)) return l.ToString(invariant);
                break;
            case SpecialType.System_UInt64:
                if (ulong.TryParse(raw, NumberStyles.Integer, invariant, out var ul)) return ul.ToString(invariant);
                break;
            case SpecialType.System_Decimal:
                if (decimal.TryParse(raw, NumberStyles.Number, invariant, out var m)) return m.ToString(invariant);
                break;
            case SpecialType.System_Double:
                if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, invariant, out var d))
                    return d.ToString("R", invariant);
                break;
            case SpecialType.System_Single:
                if (float.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, invariant, out var f))
                    return f.ToString("R", invariant);
                break;
            default:
                if (target is INamedTypeSymbol { Name: "Guid", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } })
                {
                    if (Guid.TryParse(raw, out var guid)) return guid.ToString();
                    problem = "it is not a Guid";
                }

                // Dates, times, a Uri, a domain type: the runtime's to read
                return null;
        }

        problem = $"it is not a {target.Name}";
        return null;
    }

    public static string? Primitive(object value) => value switch
    {
        string text => MarkerSpecNaming.EscapeRowValue(text),
        char c => MarkerSpecNaming.EscapeRowValue(c.ToString()),
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => null
    };

    public static string? EnumMember(INamedTypeSymbol enumType, object? value)
        => enumType.GetMembers().OfType<IFieldSymbol>()
            .FirstOrDefault(field => field.HasConstantValue && Equals(field.ConstantValue, value))?.Name;

    public static ITypeSymbol UnwrapNullable(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;
}
