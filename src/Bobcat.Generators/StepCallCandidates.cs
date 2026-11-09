using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// Decides which call sites are worth binding (bobcat#815).
/// </summary>
/// <remarks>
/// <para>
/// A call can only be a step if the method it names carries a step attribute, and it can only be a
/// projected assertion if an assertion dialect might own a method of that name. Both are questions
/// about a NAME, and the name is in the syntax. So the names every step answers to are gathered once
/// per compilation, from the declarations that carry an attribute, and a call site whose name is not
/// among them is dropped without being bound.
/// </para>
/// <para>
/// The filter may keep a call that turns out not to be a step: the full check still runs on every
/// call it keeps. It must never drop one that is, so a call whose name the syntax does not give is
/// kept.
/// </para>
/// </remarks>
internal static class StepCallCandidates
{
    public static ImmutableArray<StepInterceptors.InterceptedCall> Extract(
        ImmutableArray<InvocationExpressionSyntax> invocations, Compilation compilation,
        bool projectingAssertions, CancellationToken ct)
    {
        if (invocations.Length == 0) return ImmutableArray<StepInterceptors.InterceptedCall>.Empty;

        var names = StepNamesIn(compilation, ct);
        var models = new Dictionary<SyntaxTree, SemanticModel>();
        var calls = ImmutableArray.CreateBuilder<StepInterceptors.InterceptedCall>();

        foreach (var invocation in invocations)
        {
            ct.ThrowIfCancellationRequested();

            var name = NameOf(invocation);
            if (name != null && !names.Contains(name)
                && !(projectingAssertions && ProjectedAssertions.MightBeAssertion(name))) continue;

            Binding(compilation);
            var tree = invocation.SyntaxTree;
            if (!models.TryGetValue(tree, out var model))
            {
                model = compilation.GetSemanticModel(tree);
                models[tree] = model;
            }

            if (StepInterceptors.Extract(model, invocation, ct) is { } call) calls.Add(call);
        }

        return calls.ToImmutable();
    }

    private sealed class Tally
    {
        public int Bound;
    }

    private static readonly ConditionalWeakTable<Compilation, Tally> tallies = new();

    /// <summary>
    /// How many call sites the generator bound in <paramref name="compilation"/>, across every pass that reads
    /// them (bobcat#815) — the figure the performance test pins, so a pass cannot quietly go back to binding
    /// every call in the project. Per compilation, because the test suite runs generators in parallel.
    /// </summary>
    internal static int CallsBound(Compilation compilation)
        => tallies.TryGetValue(compilation, out var tally) ? tally.Bound : 0;

    internal static void Binding(Compilation compilation)
        => Interlocked.Increment(ref tallies.GetValue(compilation, _ => new Tally()).Bound);

    internal static string? NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => null
    };

    private static readonly ConditionalWeakTable<Compilation, HashSet<string>> namesByCompilation = new();

    internal static HashSet<string> StepNamesIn(Compilation compilation, CancellationToken ct)
    {
        if (namesByCompilation.TryGetValue(compilation, out var known)) return known;

        var names = StepNames(compilation, ct);
        return namesByCompilation.GetValue(compilation, _ => names);
    }

    internal static bool MightBeStep(InvocationExpressionSyntax invocation, Compilation compilation, CancellationToken ct)
        => NameOf(invocation) is not { } name || StepNamesIn(compilation, ct).Contains(name);

    private static bool declaresStep(IMethodSymbol method)
        => StepAttributes.On(method) != null || ProjectedRoles.StatesEventModel(method);

    internal static HashSet<string> StepNames(Compilation compilation, CancellationToken ct)
    {
        var names = new HashSet<string>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            SemanticModel? model = null;
            foreach (var node in tree.GetRoot(ct).DescendantNodes())
            {
                var attributed = node switch
                {
                    MethodDeclarationSyntax method => method.AttributeLists.Count > 0,
                    LocalFunctionStatementSyntax local => local.AttributeLists.Count > 0,
                    _ => false
                };
                if (!attributed) continue;

                ct.ThrowIfCancellationRequested();
                model ??= compilation.GetSemanticModel(tree);
                if (model.GetDeclaredSymbol(node, ct) is IMethodSymbol symbol && declaresStep(symbol))
                    names.Add(symbol.Name);
            }
        }

        foreach (var assembly in AssembliesThatCouldDeclareSteps(compilation))
        {
            ct.ThrowIfCancellationRequested();
            addStepNames(assembly.GlobalNamespace, names);
        }

        return names;
    }

    // A referenced method can only carry a step attribute if its assembly can see one: it declares a
    // type of a step attribute's name, or it reaches an assembly that does. That leaves the framework
    // and almost every package unwalked.
    internal static IEnumerable<IAssemblySymbol> AssembliesThatCouldDeclareSteps(Compilation compilation)
    {
        var referenced = compilation.References
            .Select(compilation.GetAssemblyOrModuleSymbol)
            .OfType<IAssemblySymbol>()
            .ToList();

        var reaches = new Dictionary<IAssemblySymbol, bool>(SymbolEqualityComparer.Default);
        return referenced.Where(assembly => reachesStepAttribute(assembly, reaches));
    }

    private static readonly string[] eventModelAttributes =
    [
        "EventModelRoleAttribute", "EventModelTriggerAttribute", "EventModelActAttribute"
    ];

    private static bool reachesStepAttribute(IAssemblySymbol assembly, Dictionary<IAssemblySymbol, bool> reaches)
    {
        if (reaches.TryGetValue(assembly, out var known)) return known;

        reaches[assembly] = false;
        var result = StepAttributes.Names.Any(assembly.TypeNames.Contains)
                     || eventModelAttributes.Any(assembly.TypeNames.Contains)
                     || assembly.Modules.Any(module =>
                         module.ReferencedAssemblySymbols.Any(reference => reachesStepAttribute(reference, reaches)));
        reaches[assembly] = result;
        return result;
    }

    private static void addStepNames(INamespaceOrTypeSymbol container, HashSet<string> names)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is IMethodSymbol method)
            {
                if (declaresStep(method)) names.Add(method.Name);
            }
            else if (member is INamespaceOrTypeSymbol nested)
            {
                addStepNames(nested, names);
            }
        }
    }
}
