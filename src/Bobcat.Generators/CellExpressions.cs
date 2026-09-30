using System;

namespace Bobcat.Generators;

/// <summary>
/// Whether a written cell is an <b>expression</b> rather than a literal — a reserved token, a quoted
/// literal, or a relative time.
/// </summary>
/// <remarks>
/// <para>
/// The generator's copy of what <c>Bobcat.Runtime.CellValues</c> answers at run time.
/// <c>Bobcat.Generators</c> is netstandard2.0 and references nothing, so the question is asked twice,
/// the way <c>GeneratorSliceTags</c> duplicates <c>SliceTags</c>. A file of its own, with no
/// dependencies, so <c>Bobcat.Tests</c> can link it and compare the two directly — see
/// <c>CellExpressionAgreementTests</c>.
/// </para>
/// <para>
/// <b>The agreement is deliberately one-way.</b> Every cell the runtime can resolve must be one the
/// generator admits; the reverse need not hold. A cell this admits and the runtime cannot read fails
/// at run time with a sentence naming it — a bad report. A cell the runtime could read and this
/// rejects fails the BUILD over legal input — a bug the author cannot work around.
/// </para>
/// </remarks>
public static class CellExpressions
{
    /// <summary>
    /// A relative time: <c>TODAY</c>, <c>NOW</c>, and their offsets (<c>TODAY+2</c>,
    /// <c>NOW - 30 minutes</c>).
    /// </summary>
    public static bool IsRelativeTime(string text)
    {
        var upper = text.Trim().ToUpperInvariant();

        return upper == "TODAY" || upper == "NOW"
               || upper.StartsWith("TODAY+", StringComparison.Ordinal)
               || upper.StartsWith("TODAY-", StringComparison.Ordinal)
               || upper.StartsWith("TODAY ", StringComparison.Ordinal)
               || upper.StartsWith("NOW+", StringComparison.Ordinal)
               || upper.StartsWith("NOW-", StringComparison.Ordinal)
               || upper.StartsWith("NOW ", StringComparison.Ordinal);
    }

    /// <summary>The types a relative time can be read as, by their display names.</summary>
    public static bool IsTemporal(string bareTypeName)
        => bareTypeName is "System.DateTime" or "System.DateTimeOffset"
            or "System.DateOnly" or "System.TimeOnly";

    /// <summary>A reserved cell token, matched the way the expected side matches it.</summary>
    public static bool IsToken(string text, string token)
        => string.Equals(text.Trim(), token, StringComparison.OrdinalIgnoreCase);

    /// <summary>Quoted means "the literal text of this", so a cell can say the word NULL.</summary>
    public static bool IsQuoted(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length >= 2
               && trimmed.StartsWith("\"", StringComparison.Ordinal)
               && trimmed.EndsWith("\"", StringComparison.Ordinal);
    }
}
