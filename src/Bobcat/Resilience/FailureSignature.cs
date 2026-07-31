namespace Bobcat.Resilience;

/// <summary>
/// What kind of failure this was, in the one shape every front-end can supply.
/// </summary>
/// <remarks>
/// <para>
/// Matching is by <strong>type name, not <see cref="Type"/></strong>, because out of process a
/// name is all there ever is. The #43 spike found that MTP carries at most a single error-type
/// string — and tUnit erases even that — so a signature built from the wire holds one name where
/// an in-process one holds the whole inheritance chain.
/// </para>
/// <para>
/// That asymmetry is deliberate and visible rather than hidden: a hint written against a base
/// class matches in process and abstains over the wire. It degrades to "no hint applied", which
/// is the safe direction — a run never retries something the author did not describe.
/// </para>
/// </remarks>
public sealed class FailureSignature
{
    /// <summary>No failure, or a failure whose class could not be established.</summary>
    public static readonly FailureSignature None = new([], null);

    private FailureSignature(IReadOnlyList<string> typeNames, string? message)
    {
        TypeNames = typeNames;
        Message = message;
    }

    /// <summary>
    /// The failure's type and its base types, most-derived first. Empty when the front-end could
    /// not tell us — which is a real and common case, not an error.
    /// </summary>
    public IReadOnlyList<string> TypeNames { get; }

    public string? Message { get; }

    /// <summary>False when no type name was available, so no hint can possibly match.</summary>
    public bool IsKnown => TypeNames.Count > 0;

    /// <summary>Builds the full inheritance chain — the in-process case.</summary>
    public static FailureSignature FromException(Exception? exception)
    {
        if (exception is null) return None;

        var names = new List<string>();
        for (var type = exception.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            names.Add(type.FullName ?? type.Name);
        }

        return new FailureSignature(names, exception.Message);
    }

    /// <summary>
    /// Builds a signature from a type name reported by another process. One name, no chain —
    /// see the remarks on this class for why that is not a defect.
    /// </summary>
    public static FailureSignature FromReportedType(string? typeName, string? message)
        => string.IsNullOrWhiteSpace(typeName)
            ? new FailureSignature([], message)
            : new FailureSignature([typeName.Trim()], message);

    /// <summary>
    /// How closely <paramref name="typeName"/> describes this failure: 0 is the exact type, 1 its
    /// base, and so on. Returns -1 when it does not describe it at all.
    /// </summary>
    public int Rank(string typeName)
    {
        for (var i = 0; i < TypeNames.Count; i++)
        {
            if (sameType(TypeNames[i], typeName)) return i;
        }

        return -1;
    }

    public bool Matches(string typeName) => Rank(typeName) >= 0;

    /// <summary>
    /// Two type names refer to the same type when their full names match, or — because a worker
    /// may report only <c>TimeoutException</c> where the hint holds
    /// <c>System.TimeoutException</c> — when their simple names do.
    /// </summary>
    private static bool sameType(string left, string right)
        => string.Equals(left, right, StringComparison.Ordinal)
           || string.Equals(simpleName(left), simpleName(right), StringComparison.Ordinal);

    private static string simpleName(string typeName)
    {
        var lastDot = typeName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < typeName.Length - 1 ? typeName[(lastDot + 1)..] : typeName;
    }

    public override string ToString() => TypeNames.Count > 0 ? TypeNames[0] : "unknown failure";
}
