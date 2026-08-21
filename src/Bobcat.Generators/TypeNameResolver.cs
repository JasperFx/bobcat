using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Bobcat.Generators;

/// <summary>
/// Resolves a type name written in step text (<c>Account</c>, <c>Banking.Account</c>) to a
/// <c>global::</c>-qualified name against the consuming compilation. Built once per generator
/// run; the simple-name index is only materialised when a step actually captures a type.
/// </summary>
/// <remarks>
/// Searched: the compilation's own types and the public types of every referenced assembly that
/// is not a framework assembly (<c>System*</c>, <c>mscorlib</c>, <c>netstandard</c>,
/// <c>WindowsBase</c>, <c>Microsoft.CSharp</c>/<c>Microsoft.VisualBasic</c>/<c>Microsoft.Win32.*</c>).
/// Framework assemblies are skipped for speed and because a domain type never lives there; the
/// spec project's own references (the application, its message contracts) are what a slice names.
/// Exact simple-name match first, then a unique case-insensitive match.
/// </remarks>
internal sealed class TypeNameResolver
{
    private readonly Compilation _compilation;
    private Dictionary<string, List<INamedTypeSymbol>>? _bySimpleName;

    public TypeNameResolver(Compilation compilation)
    {
        _compilation = compilation;
    }

    public sealed class Resolution
    {
        /// <summary>The <c>global::</c>-qualified name, or null when unresolved/ambiguous.</summary>
        public string? Qualified { get; set; }

        /// <summary>Display names of every candidate when the name is ambiguous.</summary>
        public List<string> Candidates { get; set; } = new();
    }

    public Resolution Resolve(string name)
    {
        name = name.Trim();

        // Dotted: a full metadata name first (nested types use '+' there, as written), then a
        // namespace-qualified suffix of a type's full name.
        if (name.IndexOf('.') >= 0 || name.IndexOf('+') >= 0)
        {
            var exact = _compilation.GetTypeByMetadataName(name);
            if (exact != null) return new Resolution { Qualified = qualified(exact) };

            var simple = name.Substring(Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+')) + 1);
            var dotted = name.Replace('+', '.');
            var matches = candidatesFor(simple)
                .Where(t => t.ToDisplayString().EndsWith("." + dotted, StringComparison.Ordinal)
                            || t.ToDisplayString() == dotted)
                .ToList();
            return decide(matches);
        }

        var byName = candidatesFor(name);
        if (byName.Count > 0) return decide(byName);

        // A unique case-insensitive match is accepted; several is still ambiguous.
        var ignoringCase = index()
            .Where(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value)
            .ToList();
        return decide(ignoringCase);
    }

    private static Resolution decide(List<INamedTypeSymbol> matches)
    {
        // The same type can be reached through more than one path; de-duplicate by display name.
        var distinct = matches
            .GroupBy(t => t.ToDisplayString(), StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (distinct.Count == 1) return new Resolution { Qualified = qualified(distinct[0]) };

        return new Resolution
        {
            Candidates = distinct.Select(t => t.ToDisplayString()).OrderBy(n => n, StringComparer.Ordinal).ToList()
        };
    }

    private List<INamedTypeSymbol> candidatesFor(string simpleName)
        => index().TryGetValue(simpleName, out var list) ? list : new List<INamedTypeSymbol>();

    private Dictionary<string, List<INamedTypeSymbol>> index()
    {
        if (_bySimpleName != null) return _bySimpleName;

        var map = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);

        void Add(INamedTypeSymbol type)
        {
            if (!map.TryGetValue(type.Name, out var list))
            {
                list = new List<INamedTypeSymbol>();
                map[type.Name] = list;
            }

            list.Add(type);
        }

        void Walk(INamespaceOrTypeSymbol container, bool publicOnly)
        {
            foreach (var member in container.GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol ns:
                        Walk(ns, publicOnly);
                        break;
                    case INamedTypeSymbol type:
                        if (type.IsImplicitlyDeclared) break;
                        if (publicOnly && type.DeclaredAccessibility != Accessibility.Public) break;
                        Add(type);
                        Walk(type, publicOnly); // nested types
                        break;
                }
            }
        }

        Walk(_compilation.Assembly.GlobalNamespace, publicOnly: false);

        foreach (var reference in _compilation.References)
        {
            if (_compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
            if (isFrameworkAssembly(assembly.Name)) continue;
            Walk(assembly.GlobalNamespace, publicOnly: true);
        }

        _bySimpleName = map;
        return map;
    }

    private static bool isFrameworkAssembly(string name)
        => name == "mscorlib"
           || name == "netstandard"
           || name == "WindowsBase"
           || name == "System"
           || name.StartsWith("System.", StringComparison.Ordinal)
           || name == "Microsoft.CSharp"
           || name == "Microsoft.VisualBasic"
           || name.StartsWith("Microsoft.VisualBasic.", StringComparison.Ordinal)
           || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);

    private static string qualified(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
