using System.Reflection;
using Bobcat.Resilience;
using JasperFx.Testing;

namespace Bobcat.Runtime;

/// <summary>
/// One recovery hint a suite declares on itself, as its <see cref="SpecManifest"/> carries it
/// across the process boundary (issue #469).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the manifest, and why reflection in the worker.</b> <c>[ClearsOnRetry]</c> and its
/// siblings are compiled into the test assembly, and a supervisor never loads that assembly — it
/// drives the suite as a process. The worker can read them, because it is the process the
/// attributes live in, and it already writes a document about itself when asked
/// (<see cref="SpecManifest.PathVariable"/>). So the worker reads, the manifest carries, and the
/// supervisor turns each entry back into a <see cref="RecoveryHint"/>.
/// </para>
/// <para>
/// <b>Scope is carried as the declaration's own coordinates, not as a test id.</b> A supervisor
/// keys everything off the platform's uid, which on xUnit is an opaque hash nobody in the worker
/// can predict. What the worker <em>can</em> say is where the attribute sits — this class, this
/// method — and the supervisor joins that to the uids its discovery reported. The scope rule is
/// unchanged by the trip: assembly &lt; class &lt; method, and the narrowest declaration wins even
/// when it names a less specific type.
/// </para>
/// </remarks>
/// <param name="FailureType">
/// The exception type the hint describes, namespace-qualified. Matched on the far side exactly as
/// <see cref="RecoveryHint.FailureTypeName"/> is — which, from the wire, means against the one type
/// name the failure reported, so a hint naming a base class abstains there.
/// </param>
/// <param name="Kind">What the declaration says recovers the failure.</param>
/// <param name="Scope">Where it was declared: the assembly, a class, or one method.</param>
/// <param name="Source">Where it came from, for the report — an assembly, a class or a method.</param>
public sealed record SpecManifestHint(
    string FailureType,
    DispositionKind Kind,
    HintScope Scope,
    string Source)
{
    /// <summary>Resources to recycle, for <see cref="DispositionKind.RetryAfterRecycle"/>; null otherwise.</summary>
    public IReadOnlyList<string>? Resources { get; init; }

    /// <summary>The author's rationale, reported verbatim.</summary>
    public string? Because { get; init; }

    /// <summary>
    /// The class the hint was declared on — or, for a method, the class that owns the test — as
    /// <see cref="Type.FullName"/> spells it (a nested class joined with <c>+</c>, the same as
    /// <see cref="SpecManifestEntry.TestClass"/>). Null for an assembly-wide hint.
    /// </summary>
    public string? TestClass { get; init; }

    /// <summary>The method the hint was declared on; null unless <see cref="Scope"/> is a test.</summary>
    public string? TestMethod { get; init; }

    /// <summary>
    /// True when this hint covers the test declared as <paramref name="testClass"/>.<paramref name="testMethod"/>.
    /// </summary>
    /// <remarks>
    /// A nested class is compared with <c>+</c> and <c>.</c> treated alike, because a discovery
    /// display name may spell it either way while the manifest always says <c>+</c>.
    /// </remarks>
    public bool Covers(string? testClass, string? testMethod) => Scope switch
    {
        HintScope.Global => true,
        HintScope.Group => sameClass(TestClass, testClass),
        _ => sameClass(TestClass, testClass)
             && TestMethod is { Length: > 0 }
             && string.Equals(TestMethod, testMethod, StringComparison.Ordinal)
    };

    /// <summary>
    /// The <see cref="RecoveryHint"/> this entry stands for, limited to <paramref name="testIds"/> —
    /// the platform uids it covers — when it is narrower than the whole run.
    /// </summary>
    public RecoveryHint ToRecoveryHint(IReadOnlyCollection<string>? testIds = null) => new()
    {
        FailureTypeName = FailureType,
        Kind = Kind,
        Resources = Resources ?? [],
        Because = Because,
        Scope = Scope,
        TestIds = Scope == HintScope.Global
            ? null
            : new HashSet<string>(testIds ?? [], StringComparer.Ordinal),
        Source = Source
    };

    /// <summary>
    /// Read every recovery hint declared in <paramref name="assembly"/> — on the assembly, on its
    /// test classes and on their methods — in the order a tie should break: assembly first, then
    /// classes, then methods, each sorted by name so two listings of one build agree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attribute reading is <see cref="RecoveryHintSet.FromAttribute"/>'s, the same code the
    /// in-process runner uses, so a hint means the same thing whichever side reads it. Class hints
    /// are read with inheritance, as <see cref="RecoveryHintSet.AddFromType"/> reads them; a method
    /// hint on a base class's test is reported against every concrete class that inherits the test,
    /// because that is the class a framework reports the test under.
    /// </para>
    /// <para>
    /// <b>A malformed hint is reported and left out, not thrown.</b> In process a hint naming a
    /// non-exception type stops the run, and should. Here the manifest is a listing other things
    /// depend on — a resident runner registers its specifications from it — and one bad attribute
    /// costing the whole listing would be a far worse failure than one hint not arriving.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SpecManifestHint> ReadFrom(Assembly assembly, Action<string>? warn = null)
        => ReadFrom(
            assembly.GetName().Name ?? "assembly",
            assembly.GetCustomAttributes<RecoveryHintAttribute>(),
            loadableTypes(assembly),
            warn);

    /// <summary>
    /// <see cref="ReadFrom(Assembly, Action{string})"/> over its parts: the assembly's name, its
    /// assembly-level attributes, and the types to scan. Split out so the reading can be tested
    /// against chosen classes without stamping a run-wide hint on a whole test assembly.
    /// </summary>
    internal static IReadOnlyList<SpecManifestHint> ReadFrom(
        string assemblyName,
        IEnumerable<RecoveryHintAttribute> assemblyAttributes,
        IEnumerable<Type> candidateTypes,
        Action<string>? warn = null)
    {
        warn ??= message => Console.Error.WriteLine(message);
        var hints = new List<SpecManifestHint>();

        foreach (var attribute in assemblyAttributes)
        {
            add(hints, attribute, HintScope.Global, assemblyName, null, null, warn);
        }

        var types = candidateTypes
            .Where(type => type is { IsClass: true, IsAbstract: false } && !type.IsGenericTypeDefinition)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var type in types)
        {
            foreach (var attribute in type.GetCustomAttributes<RecoveryHintAttribute>(inherit: true))
            {
                add(hints, attribute, HintScope.Group, type.Name, type.FullName, null, warn);
            }
        }

        const BindingFlags methods = BindingFlags.Instance | BindingFlags.Static
                                     | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(methods)
                         .Where(method => method.IsDefined(typeof(RecoveryHintAttribute), inherit: true))
                         .OrderBy(method => method.Name, StringComparer.Ordinal))
            {
                foreach (var attribute in method.GetCustomAttributes<RecoveryHintAttribute>(inherit: true))
                {
                    add(hints, attribute, HintScope.Test, $"{type.Name}.{method.Name}",
                        type.FullName, method.Name, warn);
                }
            }
        }

        return hints;
    }

    private static void add(
        List<SpecManifestHint> hints,
        RecoveryHintAttribute attribute,
        HintScope scope,
        string source,
        string? testClass,
        string? testMethod,
        Action<string> warn)
    {
        try
        {
            // Through the set's own Add, so a recycle naming no resources is refused here exactly
            // as it would be in process, rather than arriving at a supervisor that then refuses it.
            var hint = new RecoveryHintSet()
                .Add(RecoveryHintSet.FromAttribute(attribute, scope, null, source))
                .Hints[0];

            hints.Add(new SpecManifestHint(hint.FailureTypeName, hint.Kind, scope, source)
            {
                Resources = hint.Resources.Count > 0 ? hint.Resources : null,
                Because = hint.Because,
                TestClass = testClass,
                TestMethod = testMethod
            });
        }
        catch (InvalidOperationException e)
        {
            warn($"Bobcat left a recovery hint out of the spec manifest: {e.Message}");
        }
    }

    private static IEnumerable<Type> loadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            // A test assembly that references something it cannot load still has classes worth
            // reading; the ones that loaded are the ones a framework could run anyway.
            return e.Types.Where(type => type is not null)!;
        }
    }

    private static bool sameClass(string? declared, string? candidate)
        => declared is { Length: > 0 } && candidate is { Length: > 0 }
           && string.Equals(declared.Replace('+', '.'), candidate.Replace('+', '.'), StringComparison.Ordinal);
}
