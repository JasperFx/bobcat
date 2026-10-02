namespace Bobcat.Runtime;

/// <summary>
/// The one identity a specification has, in every lane: <c>{Feature}/{Scenario}</c>.
/// </summary>
/// <remarks>
/// <para>
/// It was already this string in eight places — <c>SpecNodeMapping.Uid</c>, the retry budget's
/// test id, <c>WorkPlan</c>'s partition key, <c>scenario_finished</c>'s uid,
/// <c>DeclaredSteps</c>'s key, the generator's <c>SpecificationDescriptor</c>, the ledger's and
/// the timing report's — because design-time and run evidence are meant to join with no mapping
/// table. Issue #391 is the first thing to ask a suite for its identities as a *set* and to hand
/// one back as a request, so the join finally earns a name rather than a format string repeated
/// wherever it was needed.
/// </para>
/// <para>
/// <b>Comparison is ordinal and exact.</b> This is a machine identity, not a search: a monitor
/// only ever sends back an identity a runner gave it (issue #390), <c>DeclaredSteps</c> is keyed
/// ordinally, and MTP's <c>--filter-uid</c> matches exactly. Folding case here would let two
/// scenarios differing only in case collide into one, which is a worse failure than an identity
/// that does not match — the latter is reported, the former silently runs the wrong spec.
/// </para>
/// </remarks>
public static class SpecIdentity
{
    /// <summary>The separator between a feature title and a scenario title.</summary>
    /// <remarks>
    /// A feature title containing a <c>/</c> therefore yields an identity that cannot be split
    /// back apart unambiguously — which is why nothing here offers to parse one. An identity is
    /// produced from the two titles and compared whole; the pieces are read off the model, never
    /// out of the string.
    /// </remarks>
    public const char Separator = '/';

    /// <summary>The identity of a scenario, from the two titles it is made of.</summary>
    public static string Of(string featureTitle, string scenarioTitle)
        => $"{featureTitle}{Separator}{scenarioTitle}";

    /// <summary>Whether two identities name the same specification.</summary>
    public static bool Same(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>The comparer every identity-keyed collection in Bobcat uses.</summary>
    public static StringComparer Comparer => StringComparer.Ordinal;
}
