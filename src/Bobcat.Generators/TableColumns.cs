using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Bobcat.Generators;

/// <summary>
/// Whether a table column names a member of a type, decided at compile time by the same rules the
/// runtime reads it with (bobcat#415, bobcat#420) — so a renamed property is a build error
/// (BOBCAT032) rather than an <c>invalid</c> cell on the next run.
/// </summary>
/// <remarks>
/// Three runtime readers, three rules, and each check uses the reader it is guarding:
/// <list type="bullet">
/// <item><see cref="ColumnRule.Property"/> — <c>PropertyCells</c> (a property check): public
/// instance properties by their <c>[Header]</c> title or else their name, dotted per segment.</item>
/// <item><see cref="ColumnRule.Set"/> — <c>SetVerificationComparer</c>: the same titles, top level
/// only; a dotted column is never a key the comparer reads.</item>
/// <item><see cref="ColumnRule.Partial"/> — partial objects and the shipped Gherkin grammar: a
/// property or field by name or title, or a public constructor's parameter (an F# record's
/// camelCase one), dotted per segment.</item>
/// </list>
/// All are case-insensitive, as the runtime is. A collection index is refused — the runtime refuses
/// it too.
/// </remarks>
internal static class TableColumns
{
    public const int MaxDepth = 8;

    public enum ColumnRule
    {
        Property,
        Set,
        Partial
    }

    /// <summary>
    /// The columns a constant table literal names: its header cells, or for a vertical
    /// <c>| field | value |</c> table, the first cell of each row. Null when the text is not a table.
    /// </summary>
    public static List<string>? ColumnsOf(string text)
    {
        var rows = Rows(text);
        if (rows.Count == 0) return null;

        var headers = rows[0];
        if (IsVertical(headers))
            return rows.Skip(1).Select(r => r.Count > 0 ? r[0] : "").Where(c => c.Length > 0).ToList();

        return headers.Where(c => c.Length > 0).ToList();
    }

    public static bool IsVertical(IReadOnlyList<string> headers)
        => headers.Count == 2
           && string.Equals(headers[0].Trim(), "field", StringComparison.OrdinalIgnoreCase)
           && string.Equals(headers[1].Trim(), "value", StringComparison.OrdinalIgnoreCase);

    /// <summary>The rows of a pipe table, the way <c>StepTable.Parse</c> reads them.</summary>
    public static List<List<string>> Rows(string text)
    {
        var rows = new List<List<string>>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().Trim('\r');
            if (trimmed.Length == 0) continue;

            var body = trimmed;
            if (body.StartsWith("|", StringComparison.Ordinal)) body = body.Substring(1);
            if (body.EndsWith("|", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);
            if (body.Trim().Length == 0 && trimmed.IndexOf('|') < 0) continue;

            var cells = body.Split('|').Select(c => c.Trim()).ToList();
            if (rows.Count == 1 && cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '))) continue;
            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>
    /// Why <paramref name="column"/> does not name a member of <paramref name="root"/>, or null when
    /// it does — or when the check cannot be decided at compile time (a segment whose type is open to
    /// a subclass the runtime would resolve against instead).
    /// </summary>
    public static string? Problem(ITypeSymbol root, string column, ColumnRule rule, Compilation compilation)
    {
        column = column.Trim();
        if (column.IndexOf('[') >= 0 || column.IndexOf(']') >= 0)
            return $"'{column}' indexes into a collection, which a table column cannot follow";

        var segments = column.Split('.').Select(s => s.Trim()).ToArray();
        if (rule == ColumnRule.Set && segments.Length > 1)
            return $"'{column}' is a dotted path, and a set verification compares only top-level properties";

        if (segments.Length > MaxDepth)
            return $"'{column}' is {segments.Length} levels deep and the limit is {MaxDepth}";

        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            // Below the root, the value is whatever the runtime finds there — a subclass may add the
            // member — so only a closed type is checked.
            if (i > 0 && (!Checkable(current) || !IsClosed(current, compilation))) return null;

            var next = memberType(current, segments[i], rule, includeConstructors: rule == ColumnRule.Partial);
            if (next is null)
            {
                return $"no '{segments[i]}' on {current.Name} — it has {string.Join(", ", Names(current, rule))}";
            }

            current = unwrap(next);
        }

        return null;
    }

    /// <summary>A type the check can say anything about: not object, dynamic, an interface, abstract, or a type parameter.</summary>
    public static bool Checkable(ITypeSymbol? type)
        => type is INamedTypeSymbol named
           && named.SpecialType != SpecialType.System_Object
           && named.TypeKind is TypeKind.Class or TypeKind.Struct
           && !named.IsAbstract
           && named.TypeKind != TypeKind.Error;

    /// <summary>
    /// Whether the runtime can only ever see this exact type here: a struct, a sealed class, a record,
    /// or a class nothing in the compilation (or the non-framework assemblies it references) derives
    /// from. A property check resolves against the RUNTIME type, so an open class could legitimately
    /// carry the column on a subclass.
    /// </summary>
    public static bool IsClosed(ITypeSymbol type, Compilation compilation)
    {
        if (type.TypeKind == TypeKind.Struct || type.IsSealed || type.IsRecord) return true;
        return !DerivedTypes.For(compilation).Contains(type.OriginalDefinition);
    }

    /// <summary>The leaf types a property check reads as a value, never as a path to follow.</summary>
    public static bool IsScalar(ITypeSymbol type)
    {
        type = unwrap(type);
        if (type.TypeKind == TypeKind.Enum) return true;
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_DateTime:
                return true;
        }

        var name = type.ToDisplayString();
        return name is "System.Guid" or "System.DateTimeOffset" or "System.DateOnly" or "System.TimeOnly"
            or "System.TimeSpan" or "System.Uri";
    }

    public static IEnumerable<string> Names(ITypeSymbol type, ColumnRule rule)
    {
        var names = new List<string>();
        foreach (var member in instanceMembers(type))
        {
            if (member is IFieldSymbol && rule != ColumnRule.Partial) continue;
            names.Add(titleOf(member) ?? member.Name);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal);
    }

    private static ITypeSymbol? memberType(ITypeSymbol type, string name, ColumnRule rule, bool includeConstructors)
    {
        foreach (var member in instanceMembers(type))
        {
            var title = titleOf(member);
            var matches = rule == ColumnRule.Partial
                ? string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase)
                  || (title != null && string.Equals(title, name, StringComparison.OrdinalIgnoreCase))
                // A title REPLACES the name for the property check and the set comparer.
                : string.Equals(title ?? member.Name, name, StringComparison.OrdinalIgnoreCase);

            if (!matches) continue;
            if (member is IFieldSymbol && rule != ColumnRule.Partial) continue;

            return member switch
            {
                IPropertySymbol p => p.Type,
                IFieldSymbol f => f.Type,
                _ => null
            };
        }

        if (!includeConstructors || type is not INamedTypeSymbol named) return null;

        foreach (var constructor in named.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public))
        {
            foreach (var parameter in constructor.Parameters)
            {
                var title = headerOf(parameter.GetAttributes());
                if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)
                    || (title != null && string.Equals(title, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return parameter.Type;
                }
            }
        }

        return null;
    }

    private static IEnumerable<ISymbol> instanceMembers(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) continue;
                if (member is IPropertySymbol { IsIndexer: false } property)
                {
                    if (property.Name == "EqualityContract") continue;
                    if (seen.Add(property.Name)) yield return property;
                }
                else if (member is IFieldSymbol { IsImplicitlyDeclared: false } field)
                {
                    if (seen.Add(field.Name)) yield return field;
                }
            }
        }
    }

    private static string? titleOf(ISymbol member) => headerOf(member.GetAttributes());

    private static string? headerOf(IEnumerable<AttributeData> attributes)
        => attributes
            .Where(a => a.AttributeClass?.ToDisplayString() == "Bobcat.HeaderAttribute")
            .Select(a => a.ConstructorArguments.Length > 0 ? a.ConstructorArguments[0].Value as string : null)
            .FirstOrDefault(t => t != null);

    private static ITypeSymbol unwrap(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    /// <summary>Every class something derives from, per compilation, computed once on demand.</summary>
    private static class DerivedTypes
    {
        private static readonly ConditionalWeakTable<Compilation, HashSet<ITypeSymbol>> _cache = new();

        public static HashSet<ITypeSymbol> For(Compilation compilation)
            => _cache.GetValue(compilation, build);

        private static HashSet<ITypeSymbol> build(Compilation compilation)
        {
            var bases = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

            void visit(INamespaceSymbol ns)
            {
                foreach (var type in ns.GetTypeMembers()) visitType(type);
                foreach (var child in ns.GetNamespaceMembers()) visit(child);
            }

            void visitType(INamedTypeSymbol type)
            {
                for (var b = type.BaseType; b != null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
                {
                    bases.Add(b.OriginalDefinition);
                }

                foreach (var nested in type.GetTypeMembers()) visitType(nested);
            }

            visit(compilation.Assembly.GlobalNamespace);
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                if (TypeNameResolver.IsFrameworkAssembly(assembly.Name)) continue;
                visit(assembly.GlobalNamespace);
            }

            return bases;
        }
    }
}
