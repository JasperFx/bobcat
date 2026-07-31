using JasperFx.Testing;
using System.Reflection;

namespace Bobcat.Resilience;

/// <summary>
/// How narrowly a hint was declared. A narrower scope wins over a wider one.
/// </summary>
public enum HintScope
{
    /// <summary>Declared on an assembly — applies to every test in the run.</summary>
    Global = 0,

    /// <summary>Declared on a fixture or test class — applies to the tests it owns.</summary>
    Group = 1,

    /// <summary>Declared on one test.</summary>
    Test = 2
}

/// <summary>
/// One author-declared mapping from a failure class onto a recovery, independent of the attribute
/// that usually produces it.
/// </summary>
/// <remarks>
/// Separate from <see cref="RecoveryHintAttribute"/> on purpose. Attributes are compiled into the
/// assembly the tests live in, and a supervisor never loads that assembly — it drives the worker
/// as a process. So the supervisor takes its hints through this type, and the attribute is one of
/// several ways to arrive at it. It is also the shape the observed ledger (issue #44 layer 2)
/// will produce.
/// </remarks>
public sealed record RecoveryHint
{
    /// <summary>The exception type name. Simple or namespace-qualified; both match.</summary>
    public required string FailureTypeName { get; init; }

    /// <summary>What to do — a retry kind, or <see cref="DispositionKind.FailAndContinue"/>.</summary>
    public required DispositionKind Kind { get; init; }

    /// <summary>Resources to recycle, for <see cref="DispositionKind.RetryAfterRecycle"/>.</summary>
    public IReadOnlyList<string> Resources { get; init; } = [];

    /// <summary>The author's rationale, reported verbatim.</summary>
    public string? Because { get; init; }

    public HintScope Scope { get; init; } = HintScope.Global;

    /// <summary>
    /// Limits the hint to test ids starting with this string; null applies it to every test.
    /// </summary>
    /// <remarks>
    /// A prefix rather than an exact id because a test id is <c>"{Feature}/{Scenario}"</c>, so a
    /// fixture's hints scope to its feature by prefixing with <c>"{Feature}/"</c>. Deliberately a
    /// plain string: it survives the process boundary, which a <see cref="Type"/> does not.
    /// </remarks>
    public string? TestIdPrefix { get; init; }

    /// <summary>Where this came from — a fixture name, an assembly, or the ledger. For reporting.</summary>
    public string Source { get; init; } = "";

    public bool AppliesTo(string testId)
        => TestIdPrefix is null || testId.StartsWith(TestIdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads as an explanation in the run report, because that is where it ends up.</summary>
    public override string ToString()
    {
        var what = Kind switch
        {
            DispositionKind.RetryInProcess => "clears on retry",
            DispositionKind.RetryInFreshProcess => "clears in a fresh process",
            DispositionKind.RetryAfterRecycle => $"clears after recycling {string.Join(", ", Resources)}",
            _ => "never recovers"
        };

        var origin = Source.Length > 0 ? $" (declared on {Source})" : "";
        var because = Because is { Length: > 0 } ? $": {Because}" : "";

        return $"{simpleName(FailureTypeName)} {what}{origin}{because}";
    }

    private static string simpleName(string typeName)
    {
        var lastDot = typeName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < typeName.Length - 1 ? typeName[(lastDot + 1)..] : typeName;
    }
}

/// <summary>
/// The hints in force for a run, and the rule for picking between them.
/// </summary>
public sealed class RecoveryHintSet
{
    private readonly List<RecoveryHint> _hints = new();

    public IReadOnlyList<RecoveryHint> Hints => _hints;

    public bool IsEmpty => _hints.Count == 0;

    public RecoveryHintSet Add(RecoveryHint hint)
    {
        _hints.Add(validate(hint));
        return this;
    }

    public RecoveryHintSet AddRange(IEnumerable<RecoveryHint> hints)
    {
        foreach (var hint in hints) Add(hint);
        return this;
    }

    /// <summary>
    /// Reads <see cref="RecoveryHintAttribute"/>s declared on a fixture or test class.
    /// </summary>
    /// <param name="type">The class carrying the attributes.</param>
    /// <param name="testIdPrefix">
    /// Scopes the hints to the tests this class owns. For a Bobcat fixture that is
    /// <c>"{Feature}/"</c>; null makes them global.
    /// </param>
    public RecoveryHintSet AddFromType(Type type, string? testIdPrefix = null)
    {
        foreach (var attribute in type.GetCustomAttributes<RecoveryHintAttribute>(inherit: true))
        {
            Add(FromAttribute(attribute, HintScope.Group, testIdPrefix, type.Name));
        }

        return this;
    }

    /// <summary>Reads assembly-level hints — the run-wide defaults.</summary>
    public RecoveryHintSet AddFromAssembly(Assembly assembly)
    {
        foreach (var attribute in assembly.GetCustomAttributes<RecoveryHintAttribute>())
        {
            Add(FromAttribute(attribute, HintScope.Global, null, assembly.GetName().Name ?? "assembly"));
        }

        return this;
    }

    public static RecoveryHint FromAttribute(
        RecoveryHintAttribute attribute, HintScope scope, string? testIdPrefix, string source)
    {
        if (!typeof(Exception).IsAssignableFrom(attribute.FailureType))
        {
            throw new InvalidOperationException(
                $"{attribute.GetType().Name} on {source} names {attribute.FailureType.Name}, which is not an " +
                "exception type. A recovery hint describes a failure, so it must name one.");
        }

        return new RecoveryHint
        {
            FailureTypeName = attribute.FailureType.FullName ?? attribute.FailureType.Name,
            Kind = attribute.Kind,
            Resources = attribute.Resources,
            Because = attribute.Because,
            Scope = scope,
            TestIdPrefix = testIdPrefix,
            Source = source
        };
    }

    /// <summary>
    /// The hint that best describes this failure, or null when none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordered by scope first, then by how closely the hint's type describes the failure. Scope
    /// dominating is the same rule as everywhere else in Bobcat — the declaration closest to the
    /// test has the last word — so a fixture can override a run-wide default without having to
    /// know what that default names.
    /// </para>
    /// <para>
    /// Ties break toward the hint declared first, so a set stays deterministic.
    /// </para>
    /// </remarks>
    public RecoveryHint? Best(string testId, FailureSignature failure)
    {
        if (!failure.IsKnown) return null;

        RecoveryHint? best = null;
        var bestScope = (HintScope)(-1);
        var bestRank = int.MaxValue;

        foreach (var hint in _hints)
        {
            if (!hint.AppliesTo(testId)) continue;

            var rank = failure.Rank(hint.FailureTypeName);
            if (rank < 0) continue;

            if (best is null || hint.Scope > bestScope || (hint.Scope == bestScope && rank < bestRank))
            {
                best = hint;
                bestScope = hint.Scope;
                bestRank = rank;
            }
        }

        return best;
    }

    private static RecoveryHint validate(RecoveryHint hint)
    {
        if (hint.Kind == DispositionKind.RetryAfterRecycle && hint.Resources.Count == 0)
        {
            throw new InvalidOperationException(
                $"A recycle hint for {hint.FailureTypeName} on '{hint.Source}' names no resources. " +
                "Recycling nothing is just a retry — say ClearsOnRetry, or name the resource.");
        }

        if (hint.Kind is not (DispositionKind.RetryInProcess or DispositionKind.RetryInFreshProcess
            or DispositionKind.RetryAfterRecycle or DispositionKind.FailAndContinue))
        {
            throw new InvalidOperationException(
                $"A recovery hint cannot declare {hint.Kind}. Hints describe how a failure recovers, " +
                "not whether the run continues.");
        }

        return hint;
    }
}
